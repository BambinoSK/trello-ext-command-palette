using System;
using System.Collections.Generic;
using System.Text.Json;
using TrelloExtension.Models;
using Windows.Security.Credentials;

namespace TrelloExtension.Services;

/// <summary>
/// Wraps Windows Credential Locker (PasswordVault) for secure OAuth 2.0 token storage.
/// 
/// The OAuth 2.0 token set (access + refresh + expiry, see TrelloOAuthTokens) is
/// serialized to JSON and stored as a single credential:
///   Resource = "TrelloExtension"
///   UserName = "trello-oauth2-tokens"
///   Password = JSON blob (never a raw token in plain storage)
///
/// The rotating refresh token (single-use, 90-day expiry - verified from Trello
/// OAuth 2.0 docs) is overwritten on every token refresh.
/// </summary>
internal static class CredentialService
{
    private const string ResourceName = "TrelloExtension";
    private const string UserNameKey = "trello-oauth2-tokens";

    // Shared JsonSerializerOptions via the AOT-safe source-generated context.

    /// <summary>
    /// Saves (or overwrites) the OAuth 2.0 token set in Windows Credential Locker.
    /// </summary>
    public static void SaveTokens(TrelloOAuthTokens tokens)
    {
        var json = JsonSerializer.Serialize(tokens, TrelloJsonContext.Default.TrelloOAuthTokens);
        var vault = new PasswordVault();

        // Remove any existing entry first to avoid duplicates.
        RemoveTokens(vault);
        vault.Add(new PasswordCredential(ResourceName, UserNameKey, json));
    }

    /// <summary>
    /// Retrieves the stored OAuth 2.0 token set, or null if not stored / unparsable
    /// (e.g. credentials written by an older format - treated as signed out).
    /// </summary>
    public static TrelloOAuthTokens? LoadTokens()
    {
        var json = LoadRaw();
        if (json is null) return null;

        try
        {
            return JsonSerializer.Deserialize(json, TrelloJsonContext.Default.TrelloOAuthTokens);
        }
        catch (JsonException)
        {
            // Corrupt or legacy-format blob - treat as no credentials.
            return null;
        }
    }

    /// <summary>
    /// Removes the stored OAuth 2.0 tokens from Windows Credential Locker.
    /// </summary>
    public static void DeleteTokens()
    {
        var vault = new PasswordVault();
        RemoveTokens(vault);
    }

    private static string? LoadRaw()
    {
        try
        {
            var vault = new PasswordVault();
            IReadOnlyList<PasswordCredential> creds = vault.FindAllByResource(ResourceName);
            if (creds.Count == 0) return null;

            var cred = creds[0];
            cred.RetrievePassword(); // Populates the Password property - verified from docs.
            return cred.Password;
        }
        catch (Exception)
        {
            // FindAllByResource throws if no credentials exist for the resource.
            return null;
        }
    }

    private static void RemoveTokens(PasswordVault vault)
    {
        try
        {
            IReadOnlyList<PasswordCredential> creds = vault.FindAllByResource(ResourceName);
            foreach (var cred in creds)
            {
                vault.Remove(cred);
            }
        }
        catch (Exception)
        {
            // No credentials stored - nothing to remove.
        }
    }
}
