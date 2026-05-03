using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Fills a destination span with cryptographically random bytes.
/// </summary>
/// <param name="destination">The span to fill. The delegate must fill the entire span.</param>
/// <remarks>
/// <para>
/// This is a project-wide primitive reused by every type that needs entropy (for example
/// <see cref="ReplicaId"/>). Applications supply the entropy source: a software CSPRNG such as
/// <see cref="System.Security.Cryptography.RandomNumberGenerator.Fill(System.Span{byte})"/>, or a
/// TPM/HSM-backed delegate that draws from hardware entropy.
/// </para>
/// </remarks>
public delegate void FillEntropyDelegate(Span<byte> destination);
