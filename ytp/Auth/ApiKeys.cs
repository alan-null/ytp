using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Ytp.Auth;

public enum Access { None, User, Admin }

/// <summary>The access a request's API key grants. <see cref="Id"/> names the key (never its value) for rate limiting and logs.</summary>
public sealed record Caller(Access Access, string? Id);

/// <summary>Checks the X-Api-Key header against the configured keys.</summary>
public sealed class ApiKeys
{
    public const string Header = "X-Api-Key";
    private const string ItemKey = "ytp.caller";

    private readonly (byte[] Hash, Access Access, string Id)[] keys;

    public ApiKeys(IOptions<AuthOptions> options)
    {
        var o = options.Value;
        var all = o.Keys.Select((k, i) => (Key: k, Access: Access.User, Id: $"key{i}"))
            .Concat(o.AdminKeys.Select((k, i) => (Key: k, Access: Access.Admin, Id: $"admin{i}")))
            .Where(k => !string.IsNullOrWhiteSpace(k.Key))
            .ToArray();

        if (all.Length == 0)
        {
            throw new InvalidOperationException(
                $"No API keys configured. Set {AuthOptions.Section}:Keys (e.g. env {AuthOptions.Section}__Keys__0=<key>).");
        }
        if (all.FirstOrDefault(k => k.Key.Length < AuthOptions.MinSecretLength) is { Key: not null } weak)
        {
            throw new InvalidOperationException(
                $"API key {weak.Id} is shorter than {AuthOptions.MinSecretLength} characters. Generate one with: openssl rand -base64 32");
        }
        keys = all.Select(k => (Hash(k.Key), k.Access, k.Id)).ToArray();
    }

    /// <summary>Identifies the caller from the request header; the result is cached on the request.</summary>
    public Caller Identify(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var cached) && cached is Caller caller)
        {
            return caller;
        }
        caller = Check(context.Request.Headers[Header].ToString());
        context.Items[ItemKey] = caller;
        return caller;
    }

    private Caller Check(string presented)
    {
        if (presented.Length == 0)
        {
            return new Caller(Access.None, null);
        }
        // Hashing first gives equal-length inputs to the constant-time comparison,
        // and every key is compared, so timing reveals neither which key matched nor its length.
        var hash = Hash(presented);
        var match = new Caller(Access.None, null);
        foreach (var key in keys)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, key.Hash))
            {
                match = new Caller(key.Access, key.Id);
            }
        }
        return match;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
