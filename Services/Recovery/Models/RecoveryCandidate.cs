using WireContracts = Services.Sync.Contracts;

namespace Services.Recovery.Models;

/// <summary>One stored envelope version offered to a recovering device: the sealed package it may try, and the public key its recovery proof will be verified against.</summary>
public sealed record RecoveryCandidate(long Version, WireContracts.RecoveryPackage Package, byte[] VerificationSpki);
