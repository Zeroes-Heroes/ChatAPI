using Services.Sync.Contracts;

namespace Services.Authorship.Models;

/// <summary>
/// The outcome of binding one author context's public key. <paramref name="AlreadyRegistered"/> is what
/// makes a retried registration - a lost response, a reinstall, a recovered/approved device restoring
/// the SAME authorship identity - a confirmation rather than a second write; a DIFFERENT key for the
/// same reference never reaches this type at all (it is a refusal).
/// </summary>
public sealed record AuthorKeyRegistration(AuthorKeyReference Reference, bool AlreadyRegistered);
