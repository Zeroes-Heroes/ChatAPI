namespace Services.Contacts.Models;

/// <summary>
/// The blocks in force between two people, in BOTH directions, as one answer - the shape every rule of
/// plan/09-contacts-privacy.md needs: the correspondence gate is symmetric ("Лично блокиране или
/// премахване на контакт спира новата лична кореспонденция"), while the add rule
/// ("блокираният не може да добавя блокиралия го") and the profile rule are directional.
/// </summary>
/// <param name="ViewerBlockedSubject">The viewer/actor has blocked the other party.</param>
/// <param name="SubjectBlockedViewer">The other party has blocked the viewer/actor.</param>
public readonly record struct PersonalBlockPair(bool ViewerBlockedSubject, bool SubjectBlockedViewer)
{
	public static readonly PersonalBlockPair None = new(false, false);

	/// <summary>No new personal correspondence may be created while either side holds a block.</summary>
	public bool AnyBlock => ViewerBlockedSubject || SubjectBlockedViewer;
}

/// <summary>
/// What one viewer may learn about one person (plan/09-contacts-privacy.md: "Неприятелите се
/// представят като unknown във всички изходи ... Телефон, снимка и лични данни следват отношенията
/// между хората").
///
/// There is deliberately no "partially known" variant: a surface either has the relation that justifies
/// showing a person's own data, or it shows <see cref="Unknown"/>. A future profile photo is added
/// HERE, so no surface has to learn the rule a second time.
/// </summary>
public sealed record ProfileIdentity(int UserId, bool IsKnown, string Name, string Phone)
{
	/// <summary>The exact text every output uses for a person the viewer has no relation with.</summary>
	public const string UnknownName = "unknown";

	public static ProfileIdentity Unknown(int userId) => new(userId, false, UnknownName, string.Empty);

	public static ProfileIdentity Known(int userId, string name, string phone) => new(userId, true, name, phone);
}
