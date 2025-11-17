using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DynamicCertificatePinning
{
    public class CertOptions
    {
        public ECCurve CurveAlgorithm { get; set; } = ECCurve.NamedCurves.nistP384;
        public HashAlgorithmName HashAlgorithm { get; set; } = HashAlgorithmName.SHA384;
        public string? CertFileName { get; set; } = "pq_certificate.pfx";
        public string? CertPassword { get; set; } = "UWUGIRLINNEWYORK:o";
        public DateTimeOffset Expiration { get; set; } = DateTimeOffset.UtcNow.AddYears(2);
        public bool Save{ get; set; } = true;
    }
    public class CertManager
    {
        public CertOptions Options { get; set; }

        public CertManager(CertOptions opts)
        {
            Options = opts;
        }
        public X509Certificate2 GetOrCreateCert(string subjectName)
        {
            if (File.Exists(subjectName + this.Options.CertFileName))
            {
                //return X509CertificateLoader.LoadCertificateFromFile(subjectName + this.Options.CertFileName);

                return new X509Certificate2(subjectName + this.Options.CertFileName, this.Options.CertPassword, X509KeyStorageFlags.EphemeralKeySet);
            }

            return CreateCert(subjectName);
        }

        public X509Certificate2 CreateCert(string subjectName)
        {
            using var ecdsa = ECDsa.Create(Options.CurveAlgorithm);

            var req = new CertificateRequest(
                new X500DistinguishedName($"CN={subjectName}"),
                ecdsa, Options.HashAlgorithm);

            req.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(
                    certificateAuthority: false,
                    hasPathLengthConstraint: false,
                    pathLengthConstraint: 0,
                    critical: false));

            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement,
                    critical: true));

            req.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(req.PublicKey, false));


            // Add SAN extension to satisfy browsers
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(string.IsNullOrEmpty(subjectName) ? "localhost" : subjectName);
            sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback); // optional
            req.CertificateExtensions.Add(sanBuilder.Build());

            var notBefore = DateTimeOffset.UtcNow;
            var notAfter = this.Options.Expiration;

            using var tempCert = req.CreateSelfSigned(notBefore, notAfter);

            var pfxBytes = tempCert.Export(X509ContentType.Pfx, this.Options.CertPassword);

            if (this.Options.Save)
                File.WriteAllBytes(subjectName + this.Options.CertFileName, pfxBytes);

            return new X509Certificate2(pfxBytes, this.Options.CertPassword);
        }
    }
}
