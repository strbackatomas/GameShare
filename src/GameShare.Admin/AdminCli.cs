using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Admin;

/// <summary>
/// The administrator's tool for the list of verified games: make a key, add the games as they are installed on the reference PC,
/// withdraw a version, and check a published list. Only this tool ever sees the private key.
/// The steps themselves live in <see cref="TrustWorkflow"/>, shared with the GUI version of this tool.
/// </summary>
public static class AdminCli
{
    public const string Usage = """
        gameshare-admin keygen --out <folder> [--password <text>]
            Makes trust-private.key and trust-public.key. Keep the private key safe and off the players' PCs.
            The public key goes into Agent:TrustPublicKey on every PC.

        gameshare-admin add <game folder>... --key <private key file> --list <list file> [--password <text>] [--valid-days <n>]
            Scans each folder as it is on the reference PC (a clean install, no saves) and vouches for it. Creates the list or extends it.

        gameshare-admin revoke <content hash> --reason <text> --key <private key file> --list <list file> [--password <text>] [--valid-days <n>]
            Withdraws a version. PCs refuse to install it and mark it.

        gameshare-admin remove <content hash> --key <private key file> --list <list file> [--password <text>] [--valid-days <n>]
            Takes a version out of the list. It is then simply not verified, which is not the same as revoked.

        gameshare-admin show --list <list file> --pub <public key file or the key itself>
            Checks the signature and prints what the list says.

        gameshare-admin gui
            Opens the graphical version of this tool, if it was published alongside this one.

        gameshare-admin --version
            Prints the version of this build.

        The password can also be given in the environment variable GAMESHARE_KEY_PASSWORD.
        Publish the list file at an https address or on a share and give that to the PCs as Agent:TrustListSource.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<DateTimeOffset>? clock = null)
    {
        clock ??= () => DateTimeOffset.UtcNow;
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { output.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
            if (args[0] is "-v" or "--version" or "version") { output.WriteLine($"gameshare-admin {AppVersion.Current}"); return 0; }
            var (positional, options) = Parse(args[1..]);

            switch (args[0])
            {
                case "keygen": return KeyGen(options, output);
                case "add": return await AddAsync(positional, options, output, clock()).ConfigureAwait(false);
                case "revoke": return Revoke(positional, options, output, clock());
                case "remove": return Remove(positional, options, output, clock());
                case "show": return Show(options, output, clock());
                case "gui":
                    error.WriteLine("This build has no graphical tool. Publish GameShare.AdminGui alongside it, or run gameshare-admin-gui.exe directly.");
                    return 2;
                default: error.WriteLine($"Unknown command '{args[0]}'.\n\n{Usage}"); return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int KeyGen(Dictionary<string, string> options, TextWriter output)
    {
        var info = TrustWorkflow.GenerateKeys(Required(options, "out"), Password(options));
        var publicPath = Path.Combine(Path.GetDirectoryName(info.PrivateKeyPath)!, "trust-public.key");
        output.WriteLine($"Private key: {info.PrivateKeyPath}  (keep it safe, whoever has it can sign the list)");
        output.WriteLine($"Public key:  {publicPath}");
        output.WriteLine($"Key id:      {info.KeyId}");
        output.WriteLine($"Agent setting on every PC: Agent__TrustPublicKey={info.PublicKey}");
        return 0;
    }

    private static async Task<int> AddAsync(List<string> folders, Dictionary<string, string> options, TextWriter output, DateTimeOffset now)
    {
        if (folders.Count == 0) throw new ArgumentException("Give at least one game folder.");
        var (keyPath, password, listPath) = SigningInputs(options);
        var list = LoadOrStart(keyPath, password, listPath, now);

        var games = new List<TrustedGame>();
        foreach (var folder in folders)
        {
            output.WriteLine($"Scanning {folder} ...");
            var game = await TrustWorkflow.ScanAsync(folder).ConfigureAwait(false);
            games.Add(game);
            output.WriteLine($"  {game.Name} {game.Version}  {game.ContentHash}");
        }

        return Publish(TrustListEditor.Add(list, games, now, ValidFor(options)), keyPath, password, listPath, output);
    }

    private static int Revoke(List<string> positional, Dictionary<string, string> options, TextWriter output, DateTimeOffset now)
    {
        var hash = OneHash(positional);
        var reason = Required(options, "reason");
        var (keyPath, password, listPath) = SigningInputs(options);
        var list = LoadOrStart(keyPath, password, listPath, now);
        return Publish(TrustListEditor.Revoke(list, hash, reason, now, ValidFor(options)), keyPath, password, listPath, output);
    }

    private static int Remove(List<string> positional, Dictionary<string, string> options, TextWriter output, DateTimeOffset now)
    {
        var hash = OneHash(positional);
        var (keyPath, password, listPath) = SigningInputs(options);
        var list = LoadOrStart(keyPath, password, listPath, now);
        return Publish(TrustListEditor.Remove(list, hash, now, ValidFor(options)), keyPath, password, listPath, output);
    }

    private static int Show(Dictionary<string, string> options, TextWriter output, DateTimeOffset now)
    {
        var listPath = Required(options, "list");
        var pub = Required(options, "pub");
        var publicKey = File.Exists(pub) ? File.ReadAllText(pub).Trim() : pub.Trim();

        var list = TrustSigning.Open(File.ReadAllBytes(listPath), publicKey); // throws with the reason when it does not verify
        output.WriteLine($"Signature is valid, key {TrustSigning.KeyId(publicKey)}.");
        output.WriteLine($"Sequence {list.Sequence}, issued {list.IssuedAt:yyyy-MM-dd HH:mm} UTC" +
                         (list.ValidUntil is { } until ? $", valid until {until:yyyy-MM-dd}{(until < now ? "  ** EXPIRED **" : "")}" : ", no expiry"));
        output.WriteLine($"{list.Games.Count} verified:");
        foreach (var g in list.Games) output.WriteLine($"  {g.Name} {g.Version}  {g.ContentHash}");
        output.WriteLine($"{list.Revoked.Count} revoked:");
        foreach (var r in list.Revoked) output.WriteLine($"  {r.ContentHash}  {r.Reason}");
        return 0;
    }

    /// <summary>The list that is there, checked with the key that is about to sign the next one, or a new empty one.</summary>
    private static TrustPayload LoadOrStart(string keyPath, string? password, string listPath, DateTimeOffset now) =>
        TrustWorkflow.LoadOrStartList(listPath, TrustWorkflow.LoadKey(keyPath, password).PublicKey, now);

    private static int Publish(TrustPayload list, string keyPath, string? password, string listPath, TextWriter output)
    {
        TrustWorkflow.Publish(list, keyPath, password, listPath);
        output.WriteLine($"Signed list {list.Sequence} written to {listPath}: {list.Games.Count} verified, {list.Revoked.Count} revoked. Publish it where the PCs fetch it.");
        return 0;
    }

    private static (string KeyPath, string? Password, string ListPath) SigningInputs(Dictionary<string, string> options)
    {
        var keyPath = Required(options, "key");
        if (!File.Exists(keyPath)) throw new FileNotFoundException($"Private key file '{keyPath}' does not exist.");
        return (keyPath, Password(options), Required(options, "list"));
    }

    private static string? Password(Dictionary<string, string> options) =>
        options.TryGetValue("password", out var p) ? p : Environment.GetEnvironmentVariable("GAMESHARE_KEY_PASSWORD");

    private static TimeSpan? ValidFor(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("valid-days", out var text)) return null;
        if (!int.TryParse(text, out var days) || days is < 1 or > 3650) throw new ArgumentException("--valid-days must be a number of days between 1 and 3650.");
        return TimeSpan.FromDays(days);
    }

    private static string OneHash(List<string> positional)
    {
        if (positional.Count != 1) throw new ArgumentException("Give exactly one content hash.");
        return positional[0].Trim().ToLowerInvariant();
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException($"--{name} is required.");

    private static (List<string> Positional, Dictionary<string, string> Options) Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) { positional.Add(args[i]); continue; }
            var name = args[i][2..];
            if (i + 1 >= args.Length) throw new ArgumentException($"--{name} needs a value.");
            options[name] = args[++i];
        }
        return (positional, options);
    }
}
