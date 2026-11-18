// Staging-only private CA trust. Never alters machine roots or production callbacks.
using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

public static class RaceBibStagingTls {
    public static bool Validate(X509Certificate2 leaf, X509Certificate2 root, SslPolicyErrors errors) {
        if (leaf == null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
        using (var chain = new X509Chain()) {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Per-run private CA has no CRL.
            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
            return chain.Build(leaf);
        }
    }
    public static string Get(string uri, string caPem) {
        var target = new Uri(uri);
        if (target.Scheme != "https" || target.Host == "15.135.108.24" || target.Host == "racebib.vreboton.com")
            throw new InvalidOperationException("Production/non-TLS staging request forbidden");
        using (var root = X509Certificate2.CreateFromPem(caPem))
        using (var handler = new HttpClientHandler()) {
            handler.AllowAutoRedirect = false;
            handler.UseProxy = false;
            handler.ServerCertificateCustomValidationCallback = (request, leaf, chain, errors) => Validate(leaf, root, errors);
            using (var client = new HttpClient(handler)) {
                client.Timeout = TimeSpan.FromSeconds(30);
                using (var response = client.GetAsync(target).GetAwaiter().GetResult()) {
                    if ((int)response.StatusCode != 200) throw new InvalidOperationException("Staging HTTPS expected exact 200");
                    return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
        }
    }
}
