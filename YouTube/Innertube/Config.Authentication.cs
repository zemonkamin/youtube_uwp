using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using YouTube.Innertube;

public static partial class Config
{
    private static readonly object AccessTokenOwnersGate = new object();
    private static readonly Dictionary<string, string> AccessTokenOwners = new Dictionary<string, string>(StringComparer.Ordinal);

    private static void RememberAccessTokenOwner(string accessToken, string refreshToken)
    {
        lock (AccessTokenOwnersGate)
        {
            if (AccessTokenOwners.Count >= 16) AccessTokenOwners.Clear();
            AccessTokenOwners[accessToken] = refreshToken;
        }
    }

    private static void ClearAccessTokenOwners()
    {
        lock (AccessTokenOwnersGate) AccessTokenOwners.Clear();
    }

    internal static string GetRefreshTokenForAccessToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return string.Empty;
        lock (AccessTokenOwnersGate)
        {
            string refreshToken;
            return AccessTokenOwners.TryGetValue(accessToken, out refreshToken) ? refreshToken : string.Empty;
        }
    }

    internal static async Task<string> RefreshRejectedAccessTokenAsync(string rejectedToken, string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return string.Empty;
        await _tokenRefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            LoadUserToken();
            // Do not replay an old account's request after sign-out/account switching.
            if (!string.Equals(UserToken, refreshToken, StringComparison.Ordinal)) return string.Empty;
            if (IsAccessTokenCacheValid(refreshToken)
                && !string.Equals(_cachedAccessToken, rejectedToken, StringComparison.Ordinal))
                return _cachedAccessToken;

            ClearCachedAccessToken();
            var token = await OAuthClient.RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken)
                || !string.Equals(UserToken, refreshToken, StringComparison.Ordinal)) return string.Empty;
            _cachedAccessTokenRefreshToken = refreshToken;
            _cachedAccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, token.ExpiresInSeconds - 60));
            _cachedAccessToken = token.AccessToken;
            RememberAccessTokenOwner(token.AccessToken, refreshToken);
            PersistCachedAccessToken(refreshToken);
            System.Diagnostics.Debug.WriteLine("[OAuth] Rejected access token refreshed automatically.");
            return token.AccessToken;
        }
        finally { _tokenRefreshGate.Release(); }
    }
}
