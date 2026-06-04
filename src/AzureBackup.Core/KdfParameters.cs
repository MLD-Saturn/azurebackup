namespace AzureBackup.Core;

/// <summary>
/// Canonical Argon2id key-derivation parameters and salt size shared by every
/// KDF call site. Previously these were redeclared independently in
/// <c>EncryptionService</c> (the Azure blob-encryption key) and
/// <c>SqliteBackend</c> (the SQLCipher database-unlock key), with a comment in
/// each asking the reader to keep them "identical". Centralizing them turns that
/// hand-maintained promise into a single source of truth: a divergence between
/// the two paths would be a silent security regression (one path weakening its
/// work factor with no compile error), which is exactly the failure mode this
/// type removes.
/// <para>
/// Note that the two KDF paths still derive <em>different keys</em> from
/// <em>different salt domains</em> (the plaintext <c>.salt</c> sidecar unlocks
/// the local database; the in-database <c>config.password_salt</c> derives the
/// Azure key). This type fixes only the cost parameters and the salt byte
/// length, not the salts themselves; the salt-domain separation is deliberate
/// and unaffected.
/// </para>
/// </summary>
internal static class KdfParameters
{
    /// <summary>Argon2id lane count (degree of parallelism).</summary>
    public const int Argon2DegreeOfParallelism = 8;

    /// <summary>Argon2id working-memory size in kibibytes (65,536 KiB = 64 MB).</summary>
    public const int Argon2MemorySize = 65536;

    /// <summary>Argon2id iteration (time) cost.</summary>
    public const int Argon2Iterations = 3;

    /// <summary>Salt length in bytes for both the local-unlock and Azure-key derivations.</summary>
    public const int SaltSize = 16;
}
