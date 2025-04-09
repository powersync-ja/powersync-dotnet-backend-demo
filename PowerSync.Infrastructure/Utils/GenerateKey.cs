using System.Security.Cryptography;

namespace PowerSync.Infrastructure.Utils
{
    public class KeyPairGenerator
    {
        /// <summary>
        /// Generates a new RSA key pair and a unique key identifier (kid).
        /// </summary>
        /// <returns>A tuple containing the private key, public key, and key identifier.</returns>
        public static (string privateKey, string publicKey, string kid) GenerateKeyPair()
        {
            using var rsa = RSA.Create(2048);

            var kid = $"powersync-dev-{GenerateRandomHex(4)}";

            string privateKey = Convert.ToBase64String(rsa.ExportRSAPrivateKey());
            string publicKey = Convert.ToBase64String(rsa.ExportRSAPublicKey());

            return (privateKey, publicKey, kid);
        }

        /// <summary>
        /// Generates a random hexadecimal string of the specified byte length.
        /// </summary>
        /// <param name="byteLength">The number of random bytes to generate.</param>
        /// <returns>A lowercase hexadecimal string representation of the random bytes.</returns>
        private static string GenerateRandomHex(int byteLength)
        {
            var randomBytes = new byte[byteLength];
            RandomNumberGenerator.Fill(randomBytes);
            return Convert.ToHexString(randomBytes).ToLowerInvariant();
        }
    }
}
