using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Jose;
using PowerSync.Infrastructure.Configuration;
using PowerSync.Infrastructure.Utils;
using PowerSync.Domain.Records;

namespace PowerSync.Api.Controllers
{
    /// <summary>
    /// Controller responsible for authentication-related operations including token generation
    /// and public key retrieval for JWT verification.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly PowerSyncConfig _config;
        private readonly ILogger<AuthController> _logger;
        
        // Static RSA key pairs and key identifier that persist across requests
        private static RSA? _rsaPrivate;
        private static RSA? _rsaPublic;
        private static string? _kid;

        /// <summary>
        /// Initializes a new instance of the AuthController with configuration and logging dependencies.
        /// </summary>
        /// <param name="config">Application configuration containing PowerSync settings</param>
        /// <param name="logger">Logger for recording authentication operations</param>
        public AuthController(
            IOptions<PowerSyncConfig> config,
            ILogger<AuthController> logger)
        {
            _config = config.Value;
            _logger = logger;
            EnsureKeys();
        }

        /// <summary>
        /// Ensures RSA key pairs exist, generating them if necessary.
        /// Keys are generated once and stored statically to be reused across requests.
        /// </summary>
        private static void EnsureKeys()
        {
            // Skip generation if keys already exist
            if (_rsaPrivate != null && _rsaPublic != null && _kid != null)
                return;

            // Generate new RSA key pair and key identifier
            var (privateKeyBase64, publicKeyBase64, keyId) = KeyPairGenerator.GenerateKeyPair();
            
            // Initialize private key
            _rsaPrivate = RSA.Create();
            _rsaPrivate.ImportRSAPrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
            
            // Initialize public key
            _rsaPublic = RSA.Create();
            _rsaPublic.ImportRSAPublicKey(Convert.FromBase64String(publicKeyBase64), out _);
            
            // Store key identifier
            _kid = keyId;
        }

        /// <summary>
        /// Generates a JWT token for the specified user ID.
        /// The token includes standard claims and is signed with the RSA private key.
        /// </summary>
        /// <param name="user_id">The user identifier to include in the token's subject claim</param>
        /// <returns>
        /// 200 OK with the generated token and PowerSync URL
        /// 400 Bad Request if user_id is missing or token generation fails
        /// </returns>
        [HttpGet("token")]
        public IActionResult GenerateToken([FromQuery] string? user_id)
        {
            if (string.IsNullOrEmpty(user_id))
                return BadRequest("User ID is required");

            if (_rsaPrivate == null || _kid == null)
                return BadRequest("Unable to generate token");

            // Get the PowerSync instance URL from configuration
            string powerSyncInstanceUrl = _config.PowerSyncUrl?.TrimEnd('/') ?? throw new InvalidOperationException("PowerSync URL must be configured");

            // Create JWT payload with standard claims
            var now = DateTimeOffset.UtcNow;
            var payload = new Dictionary<string, object>
            {
                { "sub", user_id },                                   // Subject (user ID)
                { "iat", now.ToUnixTimeSeconds() },                   // Issued at timestamp
                { "exp", now.AddHours(12).ToUnixTimeSeconds() },      // Expiration (12 hours)
                { "aud", powerSyncInstanceUrl },                      // Audience (PowerSync URL)
                { "iss", _config.JwtIssuer! }                         // Issuer
            };

            // Set JWT header with algorithm and key ID
            var headers = new Dictionary<string, object>
            {
                { "alg", "RS256" },    // Algorithm (RSA + SHA-256)
                { "kid", _kid }        // Key ID for key rotation support
            };

            // Sign and encode the JWT
            string token = JWT.Encode(payload, _rsaPrivate, JwsAlgorithm.RS256, headers);

            _logger.LogInformation($"Audience value: {powerSyncInstanceUrl}");

            // Return the token and PowerSync URL
            return Ok(new TokenResponse { Token = token, PowersyncUrl = powerSyncInstanceUrl });
        }

        /// <summary>
        /// Provides the public RSA key in JWK (JSON Web Key) format for token verification.
        /// This endpoint is used by PowerSync to validate tokens.
        /// </summary>
        /// <returns>
        /// 200 OK with JWK set containing the public key
        /// 400 Bad Request if public key is not available
        /// </returns>
        [HttpGet("keys")]
        public IActionResult GetKeys()
        {
            if (_rsaPublic == null || _kid == null)
                return BadRequest("No public keys available");

            // Export public key parameters
            var rsaParams = _rsaPublic.ExportParameters(false);
            
            // Format as JWK (JSON Web Key)
            var jwk = new
            {
                kty = "RSA",                                    // Key type
                alg = "RS256",                                  // Algorithm
                kid = _kid,                                     // Key ID
                n = Base64UrlEncode(rsaParams.Modulus!),       // Modulus
                e = Base64UrlEncode(rsaParams.Exponent!)       // Exponent
            };

            // Return JWK set (array of keys)
            return Ok(new { keys = new[] { jwk } });
        }

        /// <summary>
        /// Converts binary data to Base64Url encoding (RFC 4648).
        /// Base64Url is a URL-safe variant of Base64 encoding used in JWTs.
        /// </summary>
        /// <param name="input">The binary data to encode</param>
        /// <returns>Base64Url encoded string</returns>
        private string Base64UrlEncode(byte[] input) => Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}