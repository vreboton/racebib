using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using RaceTiming.Api.Infrastructure;
using RaceTiming.Api.Authorization;
using RaceTiming.Api.Services;
using RaceTiming.Infrastructure.Identity;
using RaceTiming.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
var jwtKey = builder.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 bytes.");

builder.Services.AddDbContext<RaceTimingDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 4, 0))));

builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 12;
    options.Password.RequireDigit = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
.AddRoles<IdentityRole>()
.AddEntityFrameworkStores<RaceTimingDbContext>()
.AddSignInManager();

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "RaceBib API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer",
        BearerFormat = "JWT", In = ParameterLocation.Header
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"],
        ValidateLifetime = true, ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = ClaimTypes.Name,
        RoleClaimType = ClaimTypes.Role
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var jwtId = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
            if (string.IsNullOrWhiteSpace(jwtId))
            {
                context.Fail("Token identifier is missing.");
                return;
            }

            var db = context.HttpContext.RequestServices.GetRequiredService<RaceTimingDbContext>();
            if (await db.RevokedTokens.AnyAsync(x => x.JwtId == jwtId, context.HttpContext.RequestAborted))
                context.Fail("Token has been revoked.");
        }
    };
});

builder.Services.AddAuthorization();
builder.Services.AddScoped<IOrganizerMembershipAuthorization, OrganizerMembershipAuthorization>();
builder.Services.AddScoped<IInvitationEmailSender, SmtpInvitationEmailSender>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("invitation-public", context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            }));
});
builder.Services.AddHealthChecks();

var app = builder.Build();

// Explicit one-shot maintenance; never run migrations during normal startup.
if (args.Contains("--migrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>().Database.MigrateAsync();
    await SchemaReadiness.VerifyAsync(scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>());
    return;
}
if (args.Contains("--verify-schema"))
{
    using var scope = app.Services.CreateScope();
    var result = await SchemaReadiness.VerifyAsync(scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>());
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    return;
}
if (args.Contains("--provision-admin"))
{
    using var scope = app.Services.CreateScope();
    await SchemaReadiness.VerifyAsync(scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>());
    if (string.IsNullOrWhiteSpace(builder.Configuration["SeedAdmin:Email"]) ||
        string.IsNullOrWhiteSpace(builder.Configuration["SeedAdmin:Password"]))
        throw new InvalidOperationException("Administrator credentials are required for provisioning.");
    await DevelopmentIdentitySeeder.SeedAsync(app.Services, app.Configuration, allowExistingAccountPromotion: false);
    return;
}

if (app.Environment.IsDevelopment())
    await DevelopmentIdentitySeeder.SeedAsync(app.Services, app.Configuration);

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = 500, Title = "An unexpected error occurred.", Instance = context.Request.Path
    });
}));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
// The Compose API listens only on HTTP behind a separately exposed web service.
// Enable redirect middleware only when a deployment configures an HTTPS endpoint.
if (!string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_HTTPS_PORT"]) ||
    !string.IsNullOrWhiteSpace(builder.Configuration["HttpsRedirection:HttpsPort"]))
    app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHealthChecks("/health");
// Internal Docker-network readiness. Frontend Nginx never proxies this path to the API.
app.MapGet("/health/ready", async (RaceTimingDbContext db, CancellationToken ct) =>
{
    try { return Results.Ok(await SchemaReadiness.VerifyAsync(db, ct)); }
    catch { return Results.Json(new { status = "not-ready" }, statusCode: 503); }
}).ExcludeFromDescription();
app.Run();

public partial class Program;
