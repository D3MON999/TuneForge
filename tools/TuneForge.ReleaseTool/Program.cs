using System.Security.Cryptography;

// Makes the update signing key and signs installers. This never ships with the app.
switch (args)
{
    case ["keygen", var privatePath, var publicPath]:
        if (File.Exists(privatePath))
        {
            Console.Error.WriteLine("A key already exists. Replacing it would break updates for installed copies.");
            return 1;
        }

        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
        }

        return 0;

    case ["sign", var file, var privatePath, var signaturePath]:
        using (var key = ECDsa.Create())
        using (var stream = File.OpenRead(file))
        {
            key.ImportFromPem(File.ReadAllText(privatePath));
            File.WriteAllBytes(signaturePath, key.SignData(stream, HashAlgorithmName.SHA256));
        }

        return 0;

    default:
        Console.Error.WriteLine("Use: keygen <private-file> <public-file> | sign <file> <private-file> <signature-file>");
        return 2;
}
