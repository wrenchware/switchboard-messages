using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

// The account actions on the wire (SWITCHBOARD.md 2.6c): what the four account ports do, as methods named accounts.<service>.<verb>.
//
//   A mutation changes an account or a session (connect, sign in, restore, sign out, encryption on, a reset, history indexing, forgetting
//   avatars). Each carries a clientOpId and goes through the host's OperationJournal: at most once, a repeat answered from the journal, an
//   interrupted one an unknown outcome, never retried. After one the host appends the state it caused before it answers (the dispatcher's
//   "settle"), so the caller's next read of State is current.
//
//   A read asks a question per call (is a saved session usable, which SIM does this conversation send from, what does the phone's archive
//   know). Those that can leave a mark in the state (refreshing the encrypted connection) settle the same way.
//
//   Credentials: the cookies of a sign-in, and the pairing result, cross only as the parameters of the connect/sign-in mutation. The
//   journal keeps a hash of them and the answer, never the parameters; no log, event, state or ops.status answer carries them. The saved
//   session never leaves the host (a Messenger connect with no cookies means "use the saved one").
public static partial class ProtocolMethods
{
    // ---- Messenger (IMessengerAccount) ----
    public const string AccountsMessengerHasSavedSession = "accounts.messenger.hasSavedSession";
    public const string AccountsMessengerDescribe = "accounts.messenger.describe";
    public const string AccountsMessengerRefreshEncrypted = "accounts.messenger.refreshEncrypted";
    public const string AccountsMessengerConnect = "accounts.messenger.connect";
    public const string AccountsMessengerRestore = "accounts.messenger.restore";
    public const string AccountsMessengerEnableEncrypted = "accounts.messenger.enableEncrypted";
    public const string AccountsMessengerEnableEncryptedAfterSignIn = "accounts.messenger.enableEncryptedAfterSignIn";
    public const string AccountsMessengerSignOut = "accounts.messenger.signOut";

    // ---- Instagram (IInstagramAccount) ----
    public const string AccountsInstagramAnswersFromMemory = "accounts.instagram.answersFromMemory";
    public const string AccountsInstagramResume = "accounts.instagram.resume";
    public const string AccountsInstagramConnect = "accounts.instagram.connect";
    public const string AccountsInstagramRestore = "accounts.instagram.restore";
    public const string AccountsInstagramSignOut = "accounts.instagram.signOut";

    // ---- Google Messages (IGoogleMessagesAccount) ----
    public const string AccountsGoogleSignIn = "accounts.google.signIn";
    public const string AccountsGoogleSetHistoryIndexing = "accounts.google.setHistoryIndexing";
    public const string AccountsGoogleReset = "accounts.google.reset";
    public const string AccountsGoogleForgetAvatars = "accounts.google.forgetAvatars";
    public const string AccountsGoogleSimFor = "accounts.google.simFor";
    public const string AccountsGoogleLoadRenumbered = "accounts.google.loadRenumbered";
    public const string AccountsGoogleWarmArchive = "accounts.google.warmArchive";
    public const string AccountsGoogleKnownConversations = "accounts.google.knownConversations";
    public const string AccountsGoogleKnownChats = "accounts.google.knownChats";
    public const string AccountsGoogleAvatarKeys = "accounts.google.avatarKeys";

    // ---- Google Voice (IGoogleVoiceAccounts) ----
    public const string AccountsVoiceRestore = "accounts.voice.restore";
    public const string AccountsVoiceAdd = "accounts.voice.add";
    public const string AccountsVoiceSignOut = "accounts.voice.signOut";

    // ---- The per-conversation answers the 2.6b proxies could only get from the object in this process ----
    public const string HistoryCanReadWindow = "history.canReadWindow";
    public const string ContactsFindDirect = "contacts.findDirect";
    public const string AttachmentsCanSendBatch = "attachments.canSendBatch";
    public const string SearchScopeDescription = "search.scopeDescription";

    // A method rather than a field initializer: the catalogue (in the other part of this class) reads it while its own initializer runs.
    private static Entry[] AccountEntries() =>
    [
        new(AccountsMessengerHasSavedSession, "IMessengerAccount.HasUsableSavedSessionAsync", null, typeof(bool)),
        new(AccountsMessengerDescribe, "IMessengerAccount.DescribeAsync", null, typeof(string)),
        new(AccountsMessengerRefreshEncrypted, "IMessengerAccount.RefreshEncryptedStateAsync", null, typeof(bool)),
        new(AccountsMessengerConnect, "IMessengerAccount.ConnectAsync", typeof(MessengerConnectParams), typeof(int), true),
        new(AccountsMessengerRestore, "IMessengerAccount.RestoreAsync", typeof(AccountOpParams), typeof(AccountRestore), true),
        new(AccountsMessengerEnableEncrypted, "IMessengerAccount.EnableEncryptedAsync", typeof(AccountOpParams), typeof(FailureCode), true),
        new(AccountsMessengerEnableEncryptedAfterSignIn, "IMessengerAccount.EnableEncryptedAfterSignInAsync", typeof(AccountOpParams), typeof(FailureCode), true),
        new(AccountsMessengerSignOut, "IMessengerAccount.SignOutAsync", typeof(AccountOpParams), typeof(Done), true),

        new(AccountsInstagramAnswersFromMemory, "IInstagramAccount.AnswersFromMemory", typeof(MessageIdParams), typeof(bool)),
        new(AccountsInstagramResume, "IInstagramAccount.ResumeAsync", typeof(AccountOpParams), typeof(int), true),
        new(AccountsInstagramConnect, "IInstagramAccount.ConnectAsync", typeof(InstagramConnectParams), typeof(int), true),
        new(AccountsInstagramRestore, "IInstagramAccount.RestoreAsync", typeof(AccountOpParams), typeof(AccountRestore), true),
        new(AccountsInstagramSignOut, "IInstagramAccount.SignOutAsync", typeof(AccountOpParams), typeof(Done), true),

        new(AccountsGoogleSignIn, "IGoogleMessagesAccount.SignInAsync", typeof(GoogleSignInParams), typeof(Done), true),
        new(AccountsGoogleSetHistoryIndexing, "IGoogleMessagesAccount.SetHistoryIndexingAsync", typeof(GoogleIndexingParams), typeof(Done), true),
        new(AccountsGoogleReset, "IGoogleMessagesAccount.ResetLocalAsync", typeof(GoogleResetParams), typeof(Done), true),
        new(AccountsGoogleForgetAvatars, "IGoogleMessagesAccount.ForgetAvatarsAsync", typeof(AccountOpParams), typeof(Done), true),
        new(AccountsGoogleSimFor, "IGoogleMessagesAccount.SimFor", typeof(SimForParams), typeof(GoogleSimChip)),
        new(AccountsGoogleLoadRenumbered, "IGoogleMessagesAccount.LoadRenumberedAsync", null, typeof(Done)),
        new(AccountsGoogleWarmArchive, "IGoogleMessagesAccount.WarmArchive", null, typeof(Done)),
        new(AccountsGoogleKnownConversations, "IGoogleMessagesAccount.KnownConversations", null, typeof(IReadOnlyList<ChatSummary>)),
        new(AccountsGoogleKnownChats, "IGoogleMessagesAccount.KnownChatsForResetAsync", null, typeof(IReadOnlyList<GoogleKnownChat>)),
        new(AccountsGoogleAvatarKeys, "IGoogleMessagesAccount.AvatarCacheKeysAsync", typeof(AvatarKeysParams), typeof(IReadOnlyList<string>)),

        new(AccountsVoiceRestore, "IGoogleVoiceAccounts.RestoreAsync", typeof(AccountOpParams), typeof(IReadOnlyList<string>), true),
        new(AccountsVoiceAdd, "IGoogleVoiceAccounts.AddAsync", typeof(VoiceAddParams), typeof(GoogleVoiceAccountState), true),
        new(AccountsVoiceSignOut, "IGoogleVoiceAccounts.SignOutAsync", typeof(VoiceSignOutParams), typeof(Done), true),

        new(HistoryCanReadWindow, "IHistoryWindowReader.CanReadWindow", typeof(ChatIdParams), typeof(bool)),
        new(ContactsFindDirect, "IDirectChatLookup.FindDirectChat", typeof(FindDirectParams), typeof(FindDirectResult)),
        new(AttachmentsCanSendBatch, "IAttachmentBatchBackend.CanSendBatchAsync", typeof(CanSendBatchParams), typeof(bool)),
        new(SearchScopeDescription, "ISearchScopeInfo.SearchScopeDescription", null, typeof(string)),
    ];
}

// Params and results of the account methods. Cookies are the parameters of the sign-in mutation and nothing else: they are never
// results, never in an event or a state, and never written to a log.
public sealed record AccountOpParams(string ClientOpId) : MutationParams(ClientOpId);

/// <summary>A new sign-in's cookies, or none: connect with the saved session, which the host reads itself and never sends.</summary>
public sealed record MessengerConnectParams(string ClientOpId, Dictionary<string, string>? Cookies = null) : MutationParams(ClientOpId);
public sealed record InstagramConnectParams(string ClientOpId, Dictionary<string, string> Cookies) : MutationParams(ClientOpId);
/// <summary>
/// A Google sign-in: the cookies the helper reads, and, from a client of a server only, every Google cookie the sign-in browser held (<c>profileCookies</c>, for a
/// host that seeds a browser profile of its own, SWITCHBOARD.md 5.4). The second is an added optional member: an older host ignores it, an older client never sends it.
/// </summary>
public sealed record GoogleSignInParams(string ClientOpId, Dictionary<string, string> Cookies, bool AsNewDevice, IReadOnlyList<GoogleProfileCookie>? ProfileCookies = null) : MutationParams(ClientOpId);
public sealed record GoogleIndexingParams(string ClientOpId, bool Enabled) : MutationParams(ClientOpId);
public sealed record GoogleResetParams(string ClientOpId, string Profile) : MutationParams(ClientOpId);
public sealed record VoiceAddParams(string ClientOpId, Dictionary<string, string> Cookies, string ProfileId) : MutationParams(ClientOpId);
public sealed record VoiceSignOutParams(string ClientOpId, string Key) : MutationParams(ClientOpId);

/// <summary>Why turning encrypted conversations on did not work, as a fixed code; absent (an empty object) when it worked.</summary>
public sealed record FailureCode(string? Code = null);

public sealed record SimForParams(string? ChatId = null);
public sealed record AvatarKeysParams(IReadOnlyList<string> NativeIds);

/// <summary>People to look up existing conversations for, in one call. The answer lines up with them, a null where there is none.</summary>
public sealed record FindDirectParams(IReadOnlyList<ContactPerson> Contacts);
public sealed record FindDirectResult(IReadOnlyList<ChatSummary?> Chats);
public sealed record CanSendBatchParams(string ChatId, IReadOnlyList<AttachmentMeta> Files);
