using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

// "A new version is out" (his checklist 471): at most once a day the program reads the newest version of ITSELF where it is
// published - its Nexus page once it has one, until then the repository's latest GitHub release - and the window says so with
// a link to the download page. Nothing is downloaded or installed: the download stays the player's. The player can turn the
// check off, and a version they set aside ("Not now") is not mentioned again; the next newer one is.
public static class ProgramUpdate
{
	// its Nexus mod number once the page is published (null until then)
	public static readonly string? NexusId;

	public const string GitHubRepo = "izzybinbizzy/Tool-Runner";

	public static string Page => NexusId != null ? $"https://www.nexusmods.com/skyrimspecialedition/mods/{NexusId}?tab=files" : $"https://github.com/{GitHubRepo}/releases/latest";

	public static readonly TimeSpan Every = TimeSpan.FromDays(1);

	// what the last look found, whether the player turned the look off, and the version they set aside
	public sealed record State(DateTime At, string Newest, bool Off = false, string? SetAside = null);

	public static string File(string a_dataDir) => Path.Combine(a_dataDir, "new version.json");

	// the offline tests answer for the web
	public static Func<CancellationToken, Task<string>>? Probe { get; set; }

	public static State? Read(string a_dataDir)
	{
		try {
			return System.IO.File.Exists(File(a_dataDir)) ? JsonSerializer.Deserialize<State>(Files.ReadShared(File(a_dataDir))) : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			return null;
		}
	}

	// every change reads the file as it is now and writes it back under one lock, so a look finishing never undoes a switch-off or
	// a set-aside made while it was out on the web
	static readonly Lock Gate = new();

	static State Update(string a_dataDir, Func<State?, State> a_change)
	{
		lock (Gate) {
			var state = a_change(Read(a_dataDir));
			try {
				Directory.CreateDirectory(a_dataDir);
				Files.WriteAtomic(File(a_dataDir), JsonSerializer.Serialize(state));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// not kept: the next start looks again
			}
			return state;
		}
	}

	public static bool IsOff(string a_dataDir) => Read(a_dataDir)?.Off == true;

	public static void SetOff(string a_dataDir, bool a_off) =>
		Update(a_dataDir, s => (s ?? new State(DateTime.MinValue, "")) with { Off = a_off });

	public static void SetAside(string a_dataDir, string a_version) =>
		Update(a_dataDir, s => (s ?? new State(DateTime.MinValue, a_version)) with { SetAside = a_version });

	// the newer version to tell the player about, or null (none, the look is off, it was set aside, or the web did not answer -
	// a failed look is never an error on screen and is tried again on the next start)
	public static async Task<string?> Newer(string a_dataDir, string a_current, DateTime a_now, CancellationToken a_ct)
	{
		var state = Read(a_dataDir);
		if (state?.Off == true) {
			return null;
		}
		var newest = state?.Newest ?? "";
		var due = state == null || a_now < state.At || a_now - state.At >= Every;
		if (due) {
			try {
				newest = await (Probe?.Invoke(a_ct) ?? ToolUpdates.Newest(NexusId, GitHubRepo, a_ct)).ConfigureAwait(false);
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException or NotSupportedException) {
				// a look that failed (offline, no release yet) counts as the day's look too: asked again tomorrow, never each start
				Update(a_dataDir, s => (s ?? new State(a_now, "")) with { At = a_now, Newest = "" });
				return null;
			}
			var found = newest;
			state = Update(a_dataDir, s => (s ?? new State(a_now, found)) with { At = a_now, Newest = found });
		}
		if (newest == "" || !ToolUpdates.IsNewer(a_current, newest) || state?.Off == true) {
			return null;
		}
		return state?.SetAside is { } aside && !ToolUpdates.IsNewer(aside, newest) ? null : newest;
	}

	// ---- download and install (his list of 2026-11-16, point 1: a new version taken and put in place from inside the program)
	// Only while the newest version comes from GitHub: a Nexus download needs the player's own key, so there it stays "Open
	// the download page". The release's zip is fetched into the data folder's Updates, checked (its size, GitHub's own sha-256
	// of it where GitHub gives one, plain file names only, the program inside it at the version asked for), and its files
	// are put in place of the program's own - the running program renamed aside first, which Windows allows, and every step
	// taken back if one fails. The data folder is never part of it: setups, run records and backups stay where they are.

	// a release's download: its version, the file's name and address, its size and GitHub's sha-256 of it (null: none given)
	public sealed record Release(string Version, string Asset, string Url, long Size, string? Sha256);

	// the offline tests answer for the web: the release as GitHub describes it, and the download's bytes
	public static Func<CancellationToken, Task<JsonObject?>>? ReleaseProbe { get; set; }
	public static Func<Release, CancellationToken, Task<Stream>>? AssetProbe { get; set; }

	// the version a program file says it is (the offline tests answer for a stand-in file)
	public static Func<string, string> VersionOf { get; set; } = ToolVersions.Version;

	public const long MaxDownload = 50L * 1024 * 1024;
	const long MaxUnpacked = 200L * 1024 * 1024;
	const int MaxFiles = 32;
	const string NewMark = ".new";

	public static bool Installs => NexusId == null;

	public static string ProgramFile => Brand.Name + ".exe";

	public static string UpdatesDir(string a_dataDir) => Path.Combine(a_dataDir, "Updates");

	static string Record(string a_dataDir) => Path.Combine(UpdatesDir(a_dataDir), "installed.json");

	// the same version, however each is written ("v1.1", "1.1.0.0")
	// - and never for a value with no version number in it ("unknown", "")
	// The program's own versions are compared number by number with trailing zeros dropped - never with the tools' rules, which
	// read a short version as the end of a long one ("0.1.1.0" against "1.1" was taken as the same; CodeRabbit on pull request #37)
	public static bool SameVersion(string a_a, string a_b) => Numbers(a_a) is { Count: > 0 } a && a.SequenceEqual(Numbers(a_b));

	static List<int> Numbers(string a_v)
	{
		var parts = System.Text.RegularExpressions.Regex.Matches(a_v, @"\d+").Select(m => int.TryParse(m.Value, out var n) ? n : -1).ToList();
		while (parts.Count > 1 && parts[^1] == 0) {
			parts.RemoveAt(parts.Count - 1);
		}
		return parts;
	}

	// GitHub renames an uploaded file whose name holds anything but letters, digits and a few marks ("Tool Runner 1.1.zip" is
	// listed as "Tool.Runner.1.1.zip"), and its list of a release's files gives the renamed name: two names match when they
	// are the same once every run of other characters counts as one dot
	public static bool SameAssetName(string? a_listed, string a_ours) =>
		a_listed != null && AssetKey(a_listed).Equals(AssetKey(a_ours), StringComparison.OrdinalIgnoreCase);

	static string AssetKey(string a_name)
	{
		var b = new System.Text.StringBuilder(a_name.Length);
		foreach (var c in a_name) {
			var keep = char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
			if (keep) {
				b.Append(c);
			} else if (b.Length > 0 && b[^1] != '.') {
				b.Append('.');
			}
		}
		return b.ToString().TrimEnd('.');
	}

	// the first name the zip holds twice (as Windows sees names: case does not count), or null
	static string? Twice(ZipArchive a_zip) =>
		a_zip.Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key;

	// the newest release and its download - from the program's own repository, or not at all
	public static async Task<Release> Latest(CancellationToken a_ct)
	{
		var o = await (ReleaseProbe?.Invoke(a_ct) ?? ToolUpdates.GitHubRelease(GitHubRepo, a_ct)).ConfigureAwait(false);
		var version = o?["tag_name"]?.ToString() is { Length: > 0 } tag ? tag : throw new InvalidOperationException("GitHub gave no release");
		var want = $"{Brand.Name} {version.TrimStart('v', 'V')}.zip";
		var asset = (o!["assets"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(a => SameAssetName(a["name"]?.ToString(), want));
		if (asset?["browser_download_url"]?.ToString() is not { } url) {
			throw new InvalidOperationException($"the release {version} has no download named \"{want}\" - take it from its page");
		}
		// kept under the name GitHub gives it
		var listed = asset["name"]!.ToString();
		if (!Setup.PlainName(listed)) {
			throw new InvalidOperationException($"the release's download is named \"{listed}\", which is no plain file name - take it from its page");
		}
		if (!url.StartsWith($"https://github.com/{GitHubRepo}/releases/download/", StringComparison.Ordinal)) {
			throw new InvalidOperationException("the release's download does not come from the program's own repository - not taken");
		}
		var size = asset["size"] is JsonValue sv && sv.TryGetValue<long>(out var n) ? n : 0;
		var digest = asset["digest"]?.ToString();
		return new Release(version, listed, url, size, digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].Trim() : null);
	}

	// The download fetched into the data folder's Updates and checked; returns the zip. Nothing is left behind when it fails.
	public static async Task<string> Download(Release a_r, string a_dataDir, IProgress<long>? a_progress, CancellationToken a_ct)
	{
		if (a_r.Size > MaxDownload) {
			throw new InvalidOperationException($"the download is {a_r.Size / (1024 * 1024)} MB - more than this program's own download can be; take it from its page");
		}
		var dir = UpdatesDir(a_dataDir);
		Directory.CreateDirectory(dir);
		var zip = Path.Combine(dir, a_r.Asset);
		var part = zip + ".part";
		try {
			var to = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
			await using (to.ConfigureAwait(false)) {
				if (AssetProbe != null) {
					var from = await AssetProbe(a_r, a_ct).ConfigureAwait(false);
					await using (from.ConfigureAwait(false)) {
						await ToolUpdates.CopyWatched(from, to, MaxDownload, a_progress, a_ct).ConfigureAwait(false);
					}
				} else {
					await ToolUpdates.Fetch(a_r.Url, to, MaxDownload, a_progress, a_ct).ConfigureAwait(false);
				}
			}
			if (WhyNot(part, a_r) is { } no) {
				throw new InvalidOperationException(no);
			}
			System.IO.File.Move(part, zip, true);
			return zip;
		} catch {
			TryDelete(part);
			throw;
		}
	}

	static void TryDelete(string a_file)
	{
		try {
			System.IO.File.Delete(a_file);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
		}
	}

	// why a downloaded zip is not installed (null: it is what the release says it is)
	public static string? WhyNot(string a_zip, Release a_r)
	{
		if (a_r.Sha256 is { } want) {
			using var f = System.IO.File.OpenRead(a_zip);
			var got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f));
			if (!got.Equals(want, StringComparison.OrdinalIgnoreCase)) {
				return "the download is not the file GitHub lists for this release (its check sum differs) - not installed";
			}
		}
		try {
			using var zip = System.IO.Compression.ZipFile.OpenRead(a_zip);
			if (zip.Entries.Count == 0 || zip.Entries.Count > MaxFiles || zip.Entries.Sum(e => e.Length) > MaxUnpacked) {
				return "the download does not look like this program's own (too many files, or too big) - not installed";
			}
			// its files stand in the zip's root under plain names: nothing of it can land outside the program's own folder
			if (zip.Entries.FirstOrDefault(e => e.FullName != e.Name || !Setup.PlainName(e.Name)) is { } odd) {
				return $"the download holds \"{odd.FullName}\", which is no plain file of the program - not installed";
			}
			// two entries for one file: the second would be written over the first and over the first's set-aside copy
			if (Twice(zip) is { } twice) {
				return $"the download holds {twice} twice - not installed";
			}
			if (zip.Entries.FirstOrDefault(e => e.Name.Equals(ProgramFile, StringComparison.OrdinalIgnoreCase)) is not { } exe) {
				return $"the download holds no {ProgramFile} - not installed";
			}
			// the program inside it says which version it is: looked at as a file beside the zip, never started
			var look = Path.Combine(Path.GetDirectoryName(a_zip)!, "look " + Guid.NewGuid().ToString("N")[..8] + ".exe");
			try {
				exe.ExtractToFile(look, true);
				var inside = VersionOf(look);
				return SameVersion(inside, a_r.Version) ? null : $"the program in the download says it is version {(inside == "" ? "unknown" : inside)}, not {a_r.Version} - not installed";
			} finally {
				TryDelete(look);
			}
		} catch (InvalidDataException) {
			return "the download is not a zip that reads - not installed";
		}
	}

	static string OldName(string a_file) => Path.Combine(Path.GetDirectoryName(a_file)!, Path.GetFileNameWithoutExtension(a_file) + " (old)" + Path.GetExtension(a_file));

	static bool SameBytes(Stream a_a, Stream a_b)
	{
		var x = new byte[81920];
		var y = new byte[81920];
		while (true) {
			var n = a_a.ReadAtLeast(x, x.Length, false);
			var m = a_b.ReadAtLeast(y, y.Length, false);
			if (n != m || !x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, m))) {
				return false;
			}
			if (n == 0) {
				return true;
			}
		}
	}

	// The zip's files put in place of the program's own in a_programDir; returns the new program file. Refused while a run or
	// a put-back goes. Each file is first written beside its place and read back against the zip; only then is the old one
	// renamed aside ("... (old)") and the new one given its name - the running program too: Windows lets a running program
	// be renamed, never written over. A step that fails takes every step before it back. The files set aside are on record
	// in the data folder, and the next start removes them (CleanUp).
	public static string Install(string a_zip, string a_programDir, string a_dataDir)
	{
		using var hold = Runner.HoldIfFree() ?? throw new InvalidOperationException("a run or a put-back is going - install the new version when it is done");
		var written = new List<string>();
		var swapped = new List<(string File, string? Old)>();
		try {
			using (var zip = System.IO.Compression.ZipFile.OpenRead(a_zip)) {
				if (Twice(zip) is { } twice) {
					throw new InvalidOperationException($"the download holds {twice} twice - not installed");
				}
				foreach (var e in zip.Entries) {
					if (e.FullName != e.Name || !Setup.PlainName(e.Name)) {
						throw new InvalidOperationException($"the download holds \"{e.FullName}\", which is no plain file of the program - not installed");
					}
					var fresh = Path.Combine(a_programDir, e.Name + NewMark);
					written.Add(fresh);
					e.ExtractToFile(fresh, true);
					using var inZip = e.Open();
					using var onDisk = System.IO.File.OpenRead(fresh);
					if (!SameBytes(inZip, onDisk)) {
						throw new IOException($"{e.Name} did not read back as it was written");
					}
				}
			}
			foreach (var fresh in written.ToList()) {
				var file = fresh[..^NewMark.Length];
				string? old = null;
				if (System.IO.File.Exists(file)) {
					old = OldName(file);
					System.IO.File.Move(file, old, true);
				}
				swapped.Add((file, old));
				System.IO.File.Move(fresh, file);
				written.Remove(fresh);
			}
		} catch {
			// back as it was: what was put in place goes, what was set aside returns under its own name, what was only written goes
			foreach (var (file, old) in Enumerable.Reverse(swapped)) {
				try {
					if (old != null) {
						System.IO.File.Move(old, file, true);
					} else {
						System.IO.File.Delete(file);
					}
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					// (said by the failure that brought us here; the file set aside still stands beside it under "(old)")
				}
			}
			written.ForEach(TryDelete);
			throw;
		}
		try {
			Directory.CreateDirectory(UpdatesDir(a_dataDir));
			Files.WriteAtomic(Record(a_dataDir), JsonSerializer.Serialize(new Installed(swapped.Select(s => s.Old).OfType<string>().ToList(), a_zip)));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// not on record: the files set aside stay beside the program until the player removes them
		}
		return Path.Combine(a_programDir, ProgramFile);
	}

	// what an install set aside and took its files from
	public sealed record Installed(List<string> Old, string Zip);

	// After an install, at the next start: the files it set aside and its download are removed. One that will not go yet (the
	// old program still ending) stays on record for the start after. Under the lock Install takes: an install begun in the same
	// moment sets the running program aside under the very "(old)" name this would remove, and its way back needs that file.
	// Lock taken elsewhere (a run, a put-back, an install): nothing is removed and the record stays for the next start
	// (CodeRabbit on pull request #37)
	public static void CleanUp(string a_dataDir)
	{
		using var hold = Runner.HoldIfFree();
		if (hold == null) {
			return;
		}
		Installed? was;
		try {
			was = System.IO.File.Exists(Record(a_dataDir)) ? JsonSerializer.Deserialize<Installed>(Files.ReadShared(Record(a_dataDir))) : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			return;
		}
		if (was == null) {
			return;
		}
		// only what an install itself names "(old)" or keeps in Updates: a record edited by hand removes nothing else
		// - and only in a folder that holds this program: the record names files anywhere on the PC
		var left = was.Old.Where(f => Path.GetFileNameWithoutExtension(f).EndsWith(" (old)", StringComparison.Ordinal) &&
			Path.GetDirectoryName(f) is { Length: > 0 } dir && System.IO.File.Exists(Path.Combine(dir, ProgramFile))).ToList();
		foreach (var f in left.ToList()) {
			TryDelete(f);
			if (!System.IO.File.Exists(f)) {
				left.Remove(f);
			}
		}
		if (Files.IsUnder(was.Zip, UpdatesDir(a_dataDir))) {
			TryDelete(was.Zip);
		}
		try {
			if (left.Count == 0) {
				System.IO.File.Delete(Record(a_dataDir));
			} else {
				Files.WriteAtomic(Record(a_dataDir), JsonSerializer.Serialize(was with { Old = left }));
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
		}
	}

	// ---- what is new, shown once at the first start of another version
	// the setup keys a version reads differently than the one before it, each with the version that changed it
	public static readonly IReadOnlyList<(string Since, string Text)> SetupChanges = [
		("1.0", "A setup's \"crdwCache\" is now \"directoryCache\" (changed / always / never). An older \"crdwCache\": \"refresh\" still counts as \"always\"."),
	];

	// the lines to show a player whose last start was version a_before (never "" - a first start shows nothing): this
	// version's own lines of the changelog, then every setup key read differently since a_before
	public static List<string> WhatsNew(string a_changelog, string a_before, string a_now)
	{
		var lines = new List<string>();
		var inside = false;
		foreach (var raw in a_changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
			var line = raw.TrimEnd();
			// a version's heading starts with its number ("1.1", "1.1 (not released yet)"); its lines follow until the next heading
			var heading = line.Length > 0 && char.IsDigit(line[0]);
			if (heading) {
				var named = line.Split(' ', '(')[0];
				inside = SameVersion(named, a_now);
				continue;
			}
			if (inside && line.Trim() != "") {
				lines.Add(line);
			}
		}
		var keys = SetupChanges.Where(c => ToolUpdates.IsNewer(a_before, c.Since)).Select(c => c.Text).ToList();
		if (keys.Count > 0) {
			lines.Add("");
			lines.Add("Read differently in your setups from this version on:");
			lines.AddRange(keys.Select(k => "- " + k));
		}
		return lines;
	}

	// the same off the changelog file beside the program; null when it cannot be read (then the version is not marked as seen,
	// and the next start tries again)
	public static List<string>? ReadWhatsNew(string a_changelog, string a_before, string a_now)
	{
		try {
			return WhatsNew(Files.ReadSharedText(a_changelog), a_before, a_now);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}
}
