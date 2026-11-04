FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore RaceTiming.Api/RaceTiming.Api.csproj
RUN dotnet publish RaceTiming.Api/RaceTiming.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
  CMD ["dotnet", "RaceTiming.Api.dll", "--health-check"]
ENTRYPOINT ["dotnet", "RaceTiming.Api.dll"]
