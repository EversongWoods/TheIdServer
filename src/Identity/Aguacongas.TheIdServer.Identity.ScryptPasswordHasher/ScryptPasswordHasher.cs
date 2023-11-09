﻿using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Scrypt;
using System.Text;

namespace Aguacongas.TheIdServer.Identity.ScryptPasswordHasher;

public class ScryptPasswordHasher<TUser> : IPasswordHasher<TUser> where TUser : class
{
    private readonly ScryptEncoder _encoder;
    private readonly IOptions<ScryptPasswordHasherOptions> _options;

    public ScryptPasswordHasher(ScryptEncoder encoder, IOptions<ScryptPasswordHasherOptions> options)
    {
        _encoder = encoder;
        _options = options;
    }

    public string HashPassword(TUser user, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var hash = _encoder.Encode(password);
        return Convert.ToBase64String(new byte[]
        {
            _options.Value.HashPrefix
        }
        .Concat(Encoding.UTF8.GetBytes(hash))
        .ToArray());
    }

    public PasswordVerificationResult VerifyHashedPassword(TUser user, string hashedPassword, string providedPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hashedPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(providedPassword);

        byte[] decodedHashedPassword;
        try
        {
            decodedHashedPassword = Convert.FromBase64String(hashedPassword);
        }
        catch (FormatException)
        {
            return PasswordVerificationResult.Failed;
        }

        var hash = Encoding.UTF8.GetString(decodedHashedPassword[1..]);

        var isValid = _encoder.Compare(providedPassword, hash);

        if (!isValid)
        {
            return PasswordVerificationResult.Failed;
        }

        ScryptPasswordHasher<TUser>.ExtractHeader(hash, out var _, out var iterationCount, out var blockSize, out var threadCount, out var _);
        var settings = _options.Value;
        if (settings.IterationCount != iterationCount || settings.BlockSize != blockSize || settings.ThreadCount != threadCount)
        {
            return PasswordVerificationResult.SuccessRehashNeeded;
        }
        return PasswordVerificationResult.Success;
    }

    private static void ExtractHeader(string hashedPassword, out int version, out int iterationCount, out int blockSize, out int threadCount, out byte[] saltBytes)
    {
        var parts = hashedPassword.Split('$');

        version = parts[1][1] - '0';

        if (version >= 2)
        {
            iterationCount = Convert.ToInt32(parts[2]);
            blockSize = Convert.ToInt32(parts[3]);
            threadCount = Convert.ToInt32(parts[4]);
            saltBytes = Convert.FromBase64String(parts[5]);
        }
        else
        {
            var config = Convert.ToInt64(parts[2], 16);
            iterationCount = (int)config >> 16 & 0xffff;
            blockSize = (int)config >> 8 & 0xff;
            threadCount = (int)config & 0xff;
            saltBytes = Convert.FromBase64String(parts[3]);
        }
    }

}
