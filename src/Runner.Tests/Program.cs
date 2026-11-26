using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ToolRunner;
using ToolRunner.Steps;

namespace ToolRunner.Tests;

// The offline tests: every edge case proven against a fake Mod Organizer setup in a scratch folder (no real MO2, no game,
// nothing of the PC's own touched). Exit 0 = every test passed.
//   Runner.Tests.exe [<filter>]
public static class Program
{
	static int s_pass, s_fail;
	static readonly List<string> Failures = [];

	public static int Main(string[] a_args)
	{
		if (a_args.Length > 0 && a_args[0] == "-autorun") {
			return FakeAcmos(a_args);
		}
		var filter = a_args.Length > 0 ? a_args[0] : "";
		// a game that runs on this PC is not this test's business
		Preflight.GameCheck = () => [];
		// a real run of the program on this PC holds the real run lock
		Runner.LockScope = " tests " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
		Mo2Control.GameProbe = () => [];
		// nor this PC's own state: its Mod Organizer, an update waiting, its battery, how it was started
		SystemChecks.SelfAdmin = () => false;
		SystemChecks.Mo2Unreadable = _ => false;
		SystemChecks.AdminFlagged = _ => false;
		SystemChecks.Mo2Paths = () => [];
		SystemChecks.RestartPending = () => false;
		SystemChecks.Battery = () => null;
		SystemChecks.MemoryProbe = () => (32, 16);
		SystemChecks.ControlledFolderAccess = () => false;
		SystemChecks.CloudFolders = () => [];
		SystemChecks.ProgramFolders = () => [];
		SystemChecks.IsNetwork = _ => false;
		InGame.FreeBytes = _ => 1L << 50;
		DiskForecast.Real = DiskForecast.Real with { Free = _ => 1L << 50 };
		SystemChecks.SteamRunning = () => false;
		// nor this PC's own data: the tests' data folder stands beside them before anything asks for one, so an app-data
		// folder this PC chose for every copy of the program is never taken for theirs
		// (two runs from the same folder take turns: the data folder beside them is shared, and one run's clean-up would take it
		// from under the other)
		using var oneRun = new Mutex(false, "Local\\ToolRunnerTests " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..16]);
		try {
			oneRun.WaitOne();
		} catch (AbandonedMutexException) {
			// the run before was stopped without letting go: the turn is this run's
		}
		var testData = Path.Combine(AppContext.BaseDirectory, Brand.Name + " Data");
		var madeData = !Directory.Exists(testData);
		Directory.CreateDirectory(testData);
		try {
			RunAll(filter);
		} finally {
			// (made by this run: gone with it, a test that failed too; one that was there before stays as it was)
			if (madeData) {
				try {
					Directory.Delete(testData, true);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Console.WriteLine("the tests' data folder could not be removed: " + e.Message);
				}
			}
		}
		Console.WriteLine();
		Console.WriteLine(s_fail == 0 ? $"=== ALL {s_pass} TESTS PASS ===" : $"=== {s_fail} FAILED, {s_pass} passed ===");
		return s_fail == 0 ? 0 : 1;
	}

	static void RunAll(string filter)
	{
		var tests = typeof(Program).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
			.Where(m => m.Name.StartsWith("T_", StringComparison.Ordinal) && m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.MetadataToken);
		foreach (var t in tests) {
			var sw = Stopwatch.StartNew();
			// every test begins with the program's own steps: a stand-in an earlier test put in is never this one's
			StepRegistry.PutBackOwn();
			try {
				t.Invoke(null, null);
				++s_pass;
				Console.WriteLine($"PASS  {t.Name[2..]}  ({sw.ElapsedMilliseconds} ms)");
			} catch (System.Reflection.TargetInvocationException e) {
				++s_fail;
				var msg = e.InnerException?.Message ?? e.Message;
				Failures.Add($"{t.Name[2..]}: {msg}");
				Console.WriteLine($"FAIL  {t.Name[2..]}: {msg}");
				Console.WriteLine(e.InnerException?.StackTrace?.Split('\n').FirstOrDefault()?.Trim());
			}
		}
	}

	static void Check(bool a_ok, string a_what)
	{
		if (!a_ok) {
			throw new InvalidOperationException(a_what);
		}
	}

	static void Equal<T>(T a_expected, T a_actual, string a_what)
	{
		if (!EqualityComparer<T>.Default.Equals(a_expected, a_actual)) {
			throw new InvalidOperationException($"{a_what}: expected <{a_expected}>, got <{a_actual}>");
		}
	}

	static void Throws<TE>(Action a_act, string a_what) where TE : Exception
	{
		try {
			a_act();
		} catch (TE) {
			return;
		}
		throw new InvalidOperationException(a_what + ": did not throw " + typeof(TE).Name);
	}

	[System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
	static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenForLink(string a_name, uint a_access, uint a_share, nint a_security, uint a_how, uint a_flags, nint a_template);

	[System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
	[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
	static extern bool SetReparse(Microsoft.Win32.SafeHandles.SafeFileHandle a_handle, uint a_code, byte[] a_in, int a_inSize, nint a_out, int a_outSize, out int a_returned, nint a_overlapped);

	// A junction a_link -> a_target made by Windows' own call: no command processor, so nothing in either path is read as
	// anything but itself (a "%" in a temp folder's name included)
	static void Junction(string a_link, string a_target)
	{
		var target = Path.GetFullPath(a_target);
		var named = Encoding.Unicode.GetBytes(@"\??\" + target);
		var shown = Encoding.Unicode.GetBytes(target);
		// a mount point's reparse data: tag, length, then the two names' places and lengths, then the names (each ending in 0)
		var dataLength = 8 + named.Length + 2 + shown.Length + 2;
		var data = new byte[8 + dataLength];
		BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
		BitConverter.GetBytes((ushort)dataLength).CopyTo(data, 4);
		BitConverter.GetBytes((ushort)0).CopyTo(data, 8);
		BitConverter.GetBytes((ushort)named.Length).CopyTo(data, 10);
		BitConverter.GetBytes((ushort)(named.Length + 2)).CopyTo(data, 12);
		BitConverter.GetBytes((ushort)shown.Length).CopyTo(data, 14);
		named.CopyTo(data, 16);
		shown.CopyTo(data, 16 + named.Length + 2);
		Directory.CreateDirectory(a_link);
		const uint Write = 0x40000000, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000, SetReparsePoint = 0x000900A4;
		using var h = OpenForLink(a_link, Write, 0, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
		if (h.IsInvalid || !SetReparse(h, SetReparsePoint, data, data.Length, 0, 0, out _, 0)) {
			throw new IOException($"the junction {a_link} could not be made (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
		}
	}

	// the same, handing back what was thrown (to read its message)
	static TE Thrown<TE>(Action a_act, string a_what) where TE : Exception
	{
		try {
			a_act();
		} catch (TE e) {
			return e;
		}
		throw new InvalidOperationException(a_what + ": did not throw " + typeof(TE).Name);
	}

	// when this process (the tests) started, as a run's record keeps it
	static DateTime StartedUtc()
	{
		using var me = Process.GetCurrentProcess();
		return me.StartTime.ToUniversalTime();
	}

	// ------------------------------------------------------------------ a fake Mod Organizer on disk
	sealed class FakeMo2 : IDisposable
	{
		public string Root { get; }
		// the folder Dispose removes: Root, or the folder a deeper Root was made in
		public string Top { get; }
		public string Exe => Path.Combine(Root, "MO2", "ModOrganizer.exe");
		public string Instance => Path.Combine(Root, "MO2");
		public string Game => Path.Combine(Root, "Game");
		public string ProfileDir => Path.Combine(Instance, "profiles", "Test");
		public string Mods => Path.Combine(Instance, "mods");
		public string Overwrite => Path.Combine(Instance, "overwrite");
		public string Backups => Path.Combine(Root, "Backups");
		public string Data => Path.Combine(Root, "RunnerData");
		public string SetupFile => Path.Combine(Data, "Setups", "Test.json");

		// every test runs under a folder with a space, an apostrophe, accents and a non-Latin letter in it - a player's paths can hold any
		public const string OddFolder = "Pläyer's Mödding – ü ж";

		public FakeMo2(string a_nl = "\r\n", string a_deeper = "")
		{
			Top = Path.Combine(Path.GetTempPath(), "Tool Runner tests", OddFolder + " " + Guid.NewGuid().ToString("N")[..8]);
			Root = Path.Combine(Top, a_deeper);
			Directory.CreateDirectory(Path.Combine(Game, "Data"));
			Directory.CreateDirectory(ProfileDir);
			Directory.CreateDirectory(Mods);
			Directory.CreateDirectory(Overwrite);
			File.WriteAllText(Exe, "");
			File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
				$"[General]\r\ngameName=Skyrim Special Edition\r\ngamePath=@ByteArray({Game.Replace("\\", "\\\\")})\r\nselected_profile=@ByteArray(Test)\r\n" +
				"[customExecutables]\r\nsize=1\r\n1\\title=SKSE\r\n1\\binary=" + Path.Combine(Game, "skse64_loader.exe").Replace('\\', '/') + "\r\n1\\arguments=\r\n" +
				"[Settings]\r\nbase_directory=" + Instance.Replace('\\', '/') + "\r\n");
			Plugin(Path.Combine(Game, "Data", "Skyrim.esm"), true);
			Mod("Base Mod", ("BaseMod.esp", false, []));
			Mod("Master Mod", ("MasterMod.esm", true, []));
			Mod("Output A", ("OutA.esp", false, []));
			Mod("Output B", ("OutB.esp", false, ["OutA.esp"]));
			Mod("Needs B", ("NeedsB.esp", false, ["OutB.esp"]));
			Mod("Replaced", ("Old.esp", false, []));
			// modlist: first line = highest priority; the last line is the top of MO2's pane
			File.WriteAllText(Path.Combine(ProfileDir, "modlist.txt"),
				string.Join(a_nl, "# This file was automatically generated by Mod Organizer.", "+Needs B", "+Output B", "+Output A", "+Replaced", "+Master Mod", "+Base Mod", "*DLC: Dawnguard", "-Other_separator") + a_nl);
			File.WriteAllText(Path.Combine(ProfileDir, "plugins.txt"), string.Join(a_nl, "# comment", "*MasterMod.esm", "*BaseMod.esp", "*Old.esp", "*OutA.esp", "*OutB.esp", "*NeedsB.esp") + a_nl);
			File.WriteAllText(Path.Combine(ProfileDir, "loadorder.txt"), string.Join(a_nl, "Skyrim.esm", "MasterMod.esm", "BaseMod.esp", "Old.esp", "OutA.esp", "OutB.esp", "NeedsB.esp") + a_nl);
			File.WriteAllText(Path.Combine(ProfileDir, "archives.txt"), "");
			Directory.CreateDirectory(Path.GetDirectoryName(SetupFile)!);
		}

		public void Mod(string a_name, params (string Plugin, bool Master, string[] Masters)[] a_plugins)
		{
			var d = Path.Combine(Mods, a_name);
			Directory.CreateDirectory(d);
			File.WriteAllText(Path.Combine(d, "meta.ini"), "[General]\r\n");
			foreach (var (p, m, ms) in a_plugins) {
				Plugin(Path.Combine(d, p), m, ms);
			}
		}

		public static void Plugin(string a_path, bool a_master, params string[] a_masters)
		{
			var body = new MemoryStream();
			foreach (var m in a_masters) {
				var b = Encoding.ASCII.GetBytes(m + "\0");
				body.Write(Encoding.ASCII.GetBytes("MAST"));
				body.Write(BitConverter.GetBytes((ushort)b.Length));
				body.Write(b);
				body.Write(Encoding.ASCII.GetBytes("DATA"));
				body.Write(BitConverter.GetBytes((ushort)8));
				body.Write(new byte[8]);
			}
			var head = new byte[24];
			Encoding.ASCII.GetBytes("TES4").CopyTo(head, 0);
			BitConverter.GetBytes((uint)body.Length).CopyTo(head, 4);
			head[8] = (byte)(a_master ? 1 : 0);
			using var f = File.Create(a_path);
			f.Write(head);
			f.Write(body.ToArray());
		}

		public string Text(string a_file) => File.ReadAllText(Path.Combine(ProfileDir, a_file));

		public Setup WriteSetup(params (string Step, JsonObject Settings)[] a_steps)
		{
			var steps = new JsonArray();
			foreach (var (s, o) in a_steps) {
				steps.Add(new JsonObject { ["step"] = s, ["on"] = true, ["settings"] = o });
			}
			var root = new JsonObject {
				["schema"] = 2, ["name"] = "Test", ["mo2"] = new JsonObject { ["exe"] = Exe, ["instance"] = Instance, ["profile"] = "Test" },
				["backups"] = Backups, ["minFreeGb"] = 0, ["idleMinutes"] = 2, ["steps"] = steps,
			};
			var setup = Setup.FromJson(root, SetupFile);
			setup.Save();
			return setup;
		}

		public void Dispose()
		{
			try {
				// a test's read-only files would stop the delete
				foreach (var f in new DirectoryInfo(Top).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.IsReadOnly)) {
					f.IsReadOnly = false;
				}
				Directory.Delete(Top, true);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}
	}

	static Profile ProfileOf(FakeMo2 a_f) => new(Mo2Instance.Load(a_f.Exe, a_f.Instance), "Test", () => false);

	// ------------------------------------------------------------------ files
	static void T_AtomicWriteReplacesAndLeavesNoTemp()
	{
		using var f = new FakeMo2();
		var p = Path.Combine(f.Root, "x.txt");
		File.WriteAllText(p, "old");
		Files.WriteAtomic(p, "new");
		Equal("new", File.ReadAllText(p), "content");
		Equal(1, Directory.GetFiles(f.Root, "*x.txt*").Length, "no temp file beside it");
	}

	static void T_DecodeKeepsUtf8Utf16AndBom()
	{
		Equal("é", Files.DecodeText([0xEF, 0xBB, 0xBF, 0xC3, 0xA9]), "utf-8 with bom");
		Equal("a", Files.DecodeText([0xFF, 0xFE, 0x61, 0x00]), "utf-16");
		Equal("abc", Files.DecodeText(Encoding.UTF8.GetBytes("abc")), "plain");
	}

	static void T_UnpackAnsiTwoBytesPerChar()
	{
		var packed = Encoding.Unicode.GetString(Encoding.ASCII.GetBytes("LOD generation done."));
		Check(Native.UnpackAnsi(packed).Contains("LOD generation done", StringComparison.Ordinal), "packed ANSI read back");
		Equal("normal text", Native.UnpackAnsi("normal text"), "plain text untouched");
		// the buffer as xLODGen fills it: the packed half, then as many empty characters (its answer counts bytes)
		var bytes = Encoding.ASCII.GetBytes("[17/17] Tamriel\r\nLOD generation done.");
		var buf = new char[bytes.Length + 1];
		var half = Encoding.Unicode.GetString(bytes);
		half.CopyTo(0, buf, 0, half.Length);
		Check(Native.FromBuffer(buf, bytes.Length).Contains("LOD generation done", StringComparison.Ordinal), "packed text with an empty tail read back");
		var plain = "plain log line".ToCharArray();
		Equal("plain log line", Native.FromBuffer(plain, plain.Length), "a plain buffer untouched");
		// only a log's end is made a text: packed or not is told by the whole of it, and its last letters stand in half as many
		// characters
		var log = string.Concat(Enumerable.Repeat("[00:01] Tamriel 12 34\r\n", 400)) + "LOD generation done.";
		Equal("generation done.", Native.TailOf(log, 16), "the end of a plain text");
		var end = Native.TailOf(Encoding.Unicode.GetString(Encoding.ASCII.GetBytes(log)), 40);
		Check(end.EndsWith("12 34\r\nLOD generation done.", StringComparison.Ordinal) && end.Length is >= 40 and <= 42, "the end of a packed one, unpacked: " + end);
		Equal("", Native.TailOf("", 40), "no text");
	}

	// ------------------------------------------------------------------ MO2's ini
	static void T_QtIniReadsByteArraysAndExecutables()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Equal(f.Game, mo2.GameDir, "game folder");
		Equal(Path.Combine(f.Instance, "mods"), mo2.ModsDir, "mods folder");
		Equal("Test", mo2.SelectedProfile, "selected profile");
		Check(mo2.Executable("skse") != null, "executables found case-insensitively");
		// an Executables entry's arguments as Qt escapes them: undone once (a share's \\ stays two backslashes)
		var ini = Mo2Instance.ReadQtIni("""
			[customExecutables]
			1\arguments=-o:\"D:\\LOD\\\" -x \\\\srv\\share
			""");
		Equal(@"-o:""D:\LOD\"" -x \\srv\share", ini["customExecutables"]["1\\arguments"], "quotes and backslashes unescaped once");
	}

	// a real Mod Organizer, read only: ToolRunnerRealMo2 = its ModOrganizer.exe, ToolRunnerRealBase = its instance folder (skipped
	// when unset - a build machine has none)
	static void T_RealIniReads()
	{
		var exe = Environment.GetEnvironmentVariable("ToolRunnerRealMo2");
		if (exe == null || !File.Exists(exe)) {
			Console.WriteLine("      (skipped: ToolRunnerRealMo2 not set)");
			return;
		}
		var mo2 = Mo2Instance.Load(exe);
		if (Environment.GetEnvironmentVariable("ToolRunnerRealBase") is { } b) {
			Equal(b, mo2.BaseDir, "the instance folder");
		}
		Check(mo2.Executables.Any(e => e.Title == "SKSE"), "an SKSE entry");
		Check(Directory.Exists(mo2.GameDir), "the game folder exists");
	}

	// ------------------------------------------------------------------ the profile's files
	static void T_SetModsKeepsLineEndingsAndOtherLines()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		var before = f.Text("modlist.txt");
		var changed = p.SetMods(["Replaced"], ["Output A"]);
		Equal(1, changed.Count, "only the one that changed is reported");
		var after = f.Text("modlist.txt");
		Check(after.Contains("-Output A\r\n", StringComparison.Ordinal) && after.Contains("+Replaced\r\n", StringComparison.Ordinal), "flags written, CRLF kept");
		Equal(before.Replace("+Output A", "-Output A"), after, "every other line byte for byte");
	}

	static void T_SetModsLfFileStaysLf()
	{
		using var f = new FakeMo2("\n");
		ProfileOf(f).SetMods([], ["Base Mod"]);
		Check(!f.Text("modlist.txt").Contains('\r'), "an LF file stays LF");
	}

	static void T_SetModsRefusesUnknownAndBoth()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		Throws<KeyNotFoundException>(() => p.SetMods(["Nope"], []), "unknown mod");
		Throws<ArgumentException>(() => p.SetMods(["Base Mod"], ["Base Mod"]), "on and off at once");
	}

	static void T_ProfileRefusesWritesWhileMo2Open()
	{
		using var f = new FakeMo2();
		var p = new Profile(Mo2Instance.Load(f.Exe, f.Instance), "Test", () => true);
		Throws<InvalidOperationException>(() => p.SetMods([], ["Base Mod"]), "a write while MO2 is open");
	}

	static void T_EnsureModAddsFolderAndFirstLine()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		p.EnsureMod("New Output");
		Check(File.Exists(Path.Combine(f.Mods, "New Output", "meta.ini")), "folder with meta.ini");
		var lines = f.Text("modlist.txt").Split("\r\n");
		Equal("-New Output", lines[1], "first mod line, unticked (the bottom of MO2's pane)");
	}

	static void T_SnapshotRestoreIsByteExactAndKeepsAdded()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		var snap = p.Snapshot();
		p.EnsureMod("Added Output");
		p.SetMods([], ["Base Mod", "Output A"]);
		File.WriteAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "garbage");
		p.Restore(snap);
		Equal(Encoding.UTF8.GetString(snap["plugins.txt"]), f.Text("plugins.txt"), "plugins.txt byte for byte");
		var ml = f.Text("modlist.txt");
		Check(ml.Contains("+Base Mod", StringComparison.Ordinal) && ml.Contains("+Output A", StringComparison.Ordinal), "ticks back");
		Check(ml.Contains("-Added Output", StringComparison.Ordinal), "the output mod this run added keeps its line");
	}

	static void T_PlacePluginsRules()
	{
		using var f = new FakeMo2();
		f.Mod("PG", ("PGPatcher.esp", false, []));
		f.Mod("Dyn", ("DynDOLOD.esm", true, []), ("DynDOLOD.esp", false, []), ("Occlusion.esp", false, []));
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Dyn\r\n+PG\r\n+Base Mod\r\n+Master Mod\r\n");
		File.WriteAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "*Occlusion.esp\r\n*DynDOLOD.esp\r\n*DynDOLOD.esm\r\nPGPatcher.esp\r\n*MasterMod.esm\r\n*BaseMod.esp\r\nPG_1.esp\r\n");
		var p = ProfileOf(f);
		p.PlacePlugins([new("PGPatcher.esp", "last_master", true), new("DynDOLOD.esm", "after:PGPatcher.esp"), new("DynDOLOD.esp", "end"), new("Occlusion.esp", "end"), new("PG_1.esp", "end", true)]);
		var lines = f.Text("plugins.txt").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
		Equal("*MasterMod.esm,*PGPatcher.esp,*DynDOLOD.esm,*BaseMod.esp,*DynDOLOD.esp,*Occlusion.esp,PG_1.esp", string.Join(",", lines), "placed and ticked, a plugin nothing provides left unticked");
	}

	static void T_ActivateNewMasterGoesToEndOfMasterBlock()
	{
		using var f = new FakeMo2();
		f.Mod("New", ("NewMaster.esm", true, []), ("NewPlugin.esp", false, []));
		var p = ProfileOf(f);
		p.SetMods(["Base Mod"], []);
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+New\r\n" + f.Text("modlist.txt"));
		p.ActivatePlugins(["New"]);
		var lo = f.Text("loadorder.txt").Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
		Equal(lo.IndexOf("MasterMod.esm") + 1, lo.IndexOf("NewMaster.esm"), "new master right after the last master");
		Equal("NewPlugin.esp", lo[^1], "new plugin at the end");
		Check(f.Text("plugins.txt").Contains("*NewMaster.esm", StringComparison.Ordinal), "ticked in plugins.txt");
	}

	static void T_DependentsFollowMasterChains()
	{
		using var f = new FakeMo2();
		var dep = ProfileOf(f).Dependents(["Output A"]);
		Check(dep.ContainsKey("Output B") && dep.ContainsKey("Needs B"), "a mod needing OutA, and one needing that, go off too: " + string.Join(", ", dep.Keys));
		// a setup that spells the mod otherwise than the list finds the same dependents
		var spelt = ProfileOf(f).Dependents(["output a"]);
		Check(spelt.ContainsKey("Output B") && spelt.ContainsKey("Needs B"), "the same dependents for another spelling: " + string.Join(", ", spelt.Keys));
		// a second ticked mod that ships the same master keeps it there: nothing needs to go off with the first
		f.Mod("Copy A", ("OutA.esp", false, []));
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "+Copy A\r\n+Base Mod", StringComparison.Ordinal));
		Equal(0, ProfileOf(f).Dependents(["Output A"]).Count, "another copy of its master stays on");
		Check(ProfileOf(f).Dependents(["Output A", "Copy A"]).ContainsKey("Output B"), "every copy going off takes it along");
		// a master the game's own Data folder ships too is never missing when the only mod that also ships it goes off
		f.Mod("Output C", ("Skyrim.esm", true, []));
		f.Mod("Needs Sky", ("NeedsSky.esp", false, ["Skyrim.esm"]));
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "+Output C\r\n+Needs Sky\r\n+Base Mod", StringComparison.Ordinal));
		Equal(0, ProfileOf(f).Dependents(["Output C"]).Count, "the game's own copy of the master keeps its dependent on");
	}

	static void T_DeactivatePluginsUnticksOnly()
	{
		using var f = new FakeMo2();
		ProfileOf(f).DeactivatePlugins(["Replaced"]);
		var t = f.Text("plugins.txt");
		Check(t.Contains("\r\nOld.esp\r\n", StringComparison.Ordinal) && !t.Contains("*Old.esp", StringComparison.Ordinal), "unticked, not removed");
	}

	static void T_PluginMastersRead()
	{
		using var f = new FakeMo2();
		Equal("OutA.esp", Plugins.Masters(Path.Combine(f.Mods, "Output B", "OutB.esp")).Single(), "MAST read");
		Check(Plugins.IsMaster(Path.Combine(f.Mods, "Master Mod", "MasterMod.esm")), "ESM flag");
		Check(!Plugins.IsMaster(Path.Combine(f.Mods, "Base Mod", "BaseMod.esp")), "not a master");

		// a field longer than 65,535 bytes carries its length in a field before it: the masters after it are still found
		using var body = new MemoryStream();
		void Field(string a_type, byte[] a_data, bool a_long = false)
		{
			if (a_long) {
				body.Write("XXXX"u8);
				body.Write(BitConverter.GetBytes((ushort)4));
				body.Write(BitConverter.GetBytes((uint)a_data.Length));
			}
			body.Write(Encoding.ASCII.GetBytes(a_type));
			body.Write(BitConverter.GetBytes((ushort)(a_long ? 0 : a_data.Length)));
			body.Write(a_data);
		}
		Field("MAST", "First.esm\0"u8.ToArray());
		Field("ONAM", new byte[70000], true);
		Field("MAST", "Second.esm\0"u8.ToArray());
		var head = new byte[24];
		"TES4"u8.CopyTo(head);
		BitConverter.GetBytes((uint)body.Length).CopyTo(head, 4);
		var odd = Path.Combine(f.Root, "Odd.esp");
		File.WriteAllBytes(odd, [.. head, .. body.ToArray()]);
		Equal("First.esm, Second.esm", string.Join(", ", Plugins.Masters(odd)), "a long field is stepped over by its real length");

		// a plugin that ends inside its header is one whose masters are not known - never one that needs none
		var whole = File.ReadAllBytes(Path.Combine(f.Mods, "Output B", "OutB.esp"));
		File.WriteAllBytes(odd, whole[..30]);
		Throws<IOException>(() => Plugins.Masters(odd), "a plugin that ends inside its list of masters");
		File.WriteAllBytes(odd, whole[..10]);
		Throws<IOException>(() => Plugins.Masters(odd), "a plugin that ends inside its first bytes");
		File.WriteAllBytes(odd, [.. whole[..28], 0xFF, 0xFF, .. whole[30..]]);
		Throws<IOException>(() => Plugins.Masters(odd), "a field that runs past the header's end");
		File.WriteAllText(odd, "x");
		Equal(0, Plugins.Masters(odd).Count, "a file that is no plugin needs none");
		File.WriteAllBytes(odd, "TE"u8.ToArray());
		Throws<IOException>(() => Plugins.Masters(odd), "a plugin cut down to its first two bytes");
		var longer = (byte[])head.Clone();
		BitConverter.GetBytes((uint)body.Length + 3).CopyTo(longer, 4);
		File.WriteAllBytes(odd, [.. longer, .. body.ToArray(), 1, 2, 3]);
		Throws<IOException>(() => Plugins.Masters(odd), "bytes left over after the header's last field");
		File.WriteAllBytes(Path.Combine(f.Mods, "Needs B", "NeedsB.esp"), whole[..30]);
		Throws<IOException>(() => ProfileOf(f).Dependents(["Output A"]), "what goes off with a mod is not worked out past a plugin cut short");
	}

	// ------------------------------------------------------------------ setups and order
	static void T_OrderWarningsCatchTheOldOrder()
	{
		var old = new[] { "cleansave", "xlodgen", "acmos", "grass", "landscapefixer", "pgpatcher", "treelodgen", "texgen", "dyndolod", "synthesis", "pandora", "shaders" };
		var w = StepCatalog.OrderWarnings(old);
		Check(w.Any(x => x.StartsWith("Synthesis runs after DynDOLOD", StringComparison.Ordinal)), "Synthesis after DynDOLOD is warned");
		Check(w.Any(x => x.StartsWith("Landscape Fixer runs after xLODGen", StringComparison.Ordinal)), "Landscape Fixer after xLODGen is warned");
		Equal(2, w.Count, "those two warnings and no other: " + string.Join(" | ", w));
		Equal(0, StepCatalog.OrderWarnings(StepCatalog.DefaultOrder.ToList()).Count, "the default order is safe");
		// AnvilLOD stands where DynDOLOD stands: before the patches it reads is warned about, as it is for DynDOLOD
		var anvil = StepCatalog.OrderWarnings(["anvillod", "pgpatcher", "grass", "playsave"]);
		Check(anvil.Count == 2 && anvil.Any(x => x.StartsWith("PGPatcher runs after AnvilLOD", StringComparison.Ordinal)) && anvil.Any(x => x.StartsWith("Grass cache runs after AnvilLOD", StringComparison.Ordinal)),
			"AnvilLOD before PGPatcher and the grass cache: " + string.Join(" | ", anvil));
		Equal(StepCatalog.OrderWarnings(old).Count, StepCatalog.OrderBreaks(old).Count, "each warning is one broken rule");
		var first = StepCatalog.OrderBreaks(old)[0];
		Equal(StepCatalog.OrderWarnings(old)[0], StepCatalog.RunsAfter.Replace("{0}", StepCatalog.Name(first.Before), StringComparison.Ordinal)
			.Replace("{1}", StepCatalog.Name(first.After), StringComparison.Ordinal).Replace("{2}", first.Why, StringComparison.Ordinal), "the window's text with its three places filled is the check's own line");
	}

	static void T_PlanOnlyFromUntil()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("landscapefixer", []), ("synthesis", []), ("pgpatcher", []), ("texgen", []));
		Equal("synthesis", string.Join(",", Runner.Plan(s, "synthesis", null, null).Select(x => x.Key)), "only");
		Equal("pgpatcher,texgen", string.Join(",", Runner.Plan(s, null, "pgpatcher", null).Select(x => x.Key)), "from");
		Equal("landscapefixer,synthesis", string.Join(",", Runner.Plan(s, null, null, "synthesis").Select(x => x.Key)), "until");
		Throws<ArgumentException>(() => Runner.Plan(s, null, "texgen", "landscapefixer"), "a range the wrong way round");
		Throws<ArgumentException>(() => Runner.Plan(s, "dyndolod", null, null), "a step the setup does not have");
	}

	// PGLite ticked in the profile switches the PGPatcher step off once, with a notice, and the player's own tick of it is kept
	// until PGLite is unticked
	static void T_PgLiteUnticksPgPatcherUntilThePlayerTicksIt()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("landscapefixer", []), ("pgpatcher", []));
		Equal(null, s.ApplyPgLiteRule(false), "no PGLite: nothing changes");
		var note = s.ApplyPgLiteRule(true);
		Check(note?.Contains("PGLite", StringComparison.Ordinal) == true, "PGLite ticked: PGPatcher switched off with a notice: " + note);
		Equal("landscapefixer", string.Join(",", Runner.Plan(s, null, null, null).Select(x => x.Key)), "PGPatcher left out of the run");
		Equal(null, s.ApplyPgLiteRule(true), "still unticked: it stays off, nothing new");
		s.Root["steps"]!.AsArray().OfType<JsonObject>().First(o => Setup.Str(o, "step") == "pgpatcher")["on"] = true;
		Equal("", s.ApplyPgLiteRule(true), "the player ticked it back: kept as their choice");
		Equal("landscapefixer,pgpatcher", string.Join(",", Runner.Plan(s, null, null, null).Select(x => x.Key)), "PGPatcher runs again");
		Equal(null, s.ApplyPgLiteRule(true), "their tick stays");
		Equal("", s.ApplyPgLiteRule(false), "PGLite unticked: their choice is cleared");
		Check(s.ApplyPgLiteRule(true) is { Length: > 0 }, "PGLite ticked again later: switched off again");
		Check(s.ApplyPgLiteRule(false)?.Contains("switched on again", StringComparison.Ordinal) == true, "PGLite gone while the step was still off by the rule: on again, and said");
		Equal("landscapefixer,pgpatcher", string.Join(",", Runner.Plan(s, null, null, null).Select(x => x.Key)), "so PGPatcher runs again without PGLite");
		Equal(null, s.ApplyPgLiteRule(false), "and nothing more to do");
		// PGLite is known by its own plugin, whatever the player's mod folder is called
		var profile = ProfileOf(f);
		Check(!Runner.PgLiteTicked(profile), "no PGLite in the profile");
		f.Mod("PG Lite 2.4 (my copy)");
		Directory.CreateDirectory(Path.Combine(f.Mods, "PG Lite 2.4 (my copy)", "SKSE", "Plugins"));
		File.WriteAllText(Path.Combine(f.Mods, "PG Lite 2.4 (my copy)", "SKSE", "Plugins", "PGLite.dll"), "x");
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "-PG Lite 2.4 (my copy)\r\n+Base Mod", StringComparison.Ordinal));
		Check(!Runner.PgLiteTicked(profile), "there but unticked: not counted");
		File.WriteAllText(list, File.ReadAllText(list).Replace("-PG Lite 2.4 (my copy)", "+PG Lite 2.4 (my copy)", StringComparison.Ordinal));
		Check(Runner.PgLiteTicked(profile), "ticked under another folder name: found by its plugin");
	}

	// every box of every process the run started is seen the whole run, named, answered when the
	// runner knows it, and one of the game's that nobody can answer stops the step with its words
	static void T_EveryBoxIsNamedAndTheKnownOnesAnswered()
	{
		using var f = new FakeMo2();
		using var log = new RunLog(Path.Combine(f.Data, "box log"));
		var lines = new List<string>();
		log.Line += (t, _) => lines.Add(t);
		var open = new List<BoxWatch.Open>();
		var pressed = new List<string>();
		var shown = new List<string>();
		var ended = 0;
		BoxWatch.Open Box(long a_id, string a_image, bool a_game, string a_title, string a_text, params string[] a_buttons) =>
			new(a_id, new BoxSeen(a_image, a_game, a_title, a_text, a_buttons), b => {
				lock (pressed) {
					pressed.Add($"{a_id}:{b}");
				}
			});
		BoxWatch Watch() => new(() => open, log, shown.Add, () => ++ended) { Wait = TimeSpan.FromSeconds(60) };
		var t0 = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

		using (var w = Watch()) {
			w.LookOnce(t0);
			Equal(0, lines.Count + pressed.Count + shown.Count, "nothing open: nothing said");
			// the cache mod's question, as its own source words it
			open.Add(Box(1, "SkyrimSE.exe", true, "Cache Recursive Directory Walk", "Do you want to generate CRDW cache files?", "Yes", "No"));
			w.LookOnce(t0);
			Equal("1:Yes", string.Join(",", pressed), "its question is answered Yes");
			Check(lines.Any(l => l.StartsWith("a box is open - SkyrimSE.exe: \"Cache Recursive Directory Walk\"", StringComparison.Ordinal)), "named in the log: " + string.Join(" | ", lines));
			Check(lines.Contains(Crdw.Answered), "and why it was answered so");
			w.LookOnce(t0.AddSeconds(3));
			Equal(1, pressed.Count, "not pressed again at the next look");
			w.LookOnce(t0.AddSeconds(11));
			Equal(2, pressed.Count, "pressed again when it still stands ten seconds on");
			open.Clear();
			w.LookOnce(t0.AddSeconds(14));
			Equal(null, w.Blocked, "it closed: nothing is blocked");
			Equal(0, ended, "and the game goes on");
		}

		// the same question from a Skyrim the PLAYER started under the run's Mod Organizer: theirs to answer - only named
		pressed.Clear();
		shown.Clear();
		lines.Clear();
		using (var w = Watch()) {
			open.Add(Box(11, "SkyrimSE.exe", false, "Cache Recursive Directory Walk", "Do you want to generate CRDW cache files?", "Yes", "No"));
			w.LookOnce(t0);
			w.LookOnce(t0.AddSeconds(61));
			Equal(0, pressed.Count, "the cache mod's question in a game the player started is never answered for them");
			Check(lines.Any(l => l.StartsWith("a box is open - SkyrimSE.exe: \"Cache Recursive Directory Walk\"", StringComparison.Ordinal)) && !lines.Contains(Crdw.Answered), "it is named, and not said to be answered: " + string.Join(" | ", lines));
			Check(w.Blocked == null && ended == 0, "and it neither stops a step nor ends a game");
			open.Clear();
		}
		Equal(BoxMove.Press, KnownBoxes.Judge(new BoxSeen("SkyrimSE.exe", true, "Cache Recursive Directory Walk", "Do you want to generate CRDW cache files?", ["Yes", "No"])).Move, "the run's own game's is answered");
		Equal(BoxMove.Name, KnownBoxes.Judge(new BoxSeen("SkyrimSE.exe", false, "Cache Recursive Directory Walk", "Do you want to generate CRDW cache files?", ["Yes", "No"])).Move, "the player's game's is only named");
		// a box of the run's game that nothing here knows: closed by the button that takes nothing on,
		// unless the setup leaves such boxes to the player
		var notKnown = new BoxSeen("SkyrimSE.exe", true, "Some Mod", "Reset all of its settings?", ["Yes", "No"]);
		Equal(BoxMove.Stop, KnownBoxes.Judge(notKnown).Move, "left to the player where the setup says so: never guessed at");
		var closedBox = KnownBoxes.Judge(notKnown, true);
		Check(closedBox.Move == BoxMove.Press && closedBox.Button == "No" && closedBox.Note.Contains("does not know", StringComparison.Ordinal), "closed with its declining button, and named: " + closedBox.Note);
		Equal("Ignore", KnownBoxes.Judge(new BoxSeen("SkyrimSE.exe", true, "Error", "Something failed", ["Abort", "Retry", "Ignore"]), true).Button, "Windows' own three: Ignore");
		Equal(BoxMove.Stop, KnownBoxes.Judge(new BoxSeen("SkyrimSE.exe", true, "Working", "Packing the archives ...", ["Cancel"]), true).Move, "a box whose one button is Cancel shows something at work: never pressed");
		// a box in words, as the log and the run's notes name it: whose it is, its title, its words, its buttons
		Equal("SkyrimSE.exe: \"Error\" - Something failed [Abort / Retry / Ignore]",
			KnownBoxes.Describe(new BoxSeen("SkyrimSE.exe", true, "Error", "Something failed", ["Abort", "Retry", "Ignore"])), "a box in words");
		Equal("a program of the run: \"Note\"", KnownBoxes.Describe(new BoxSeen("", false, "Note", "  ", [])), "no program's name, no words, no buttons");
		Check(KnownBoxes.Describe(new BoxSeen("x.exe", false, "T", new string('a', 400), [])).EndsWith("a ...", StringComparison.Ordinal), "a long text cut short");
		// a plugin by its ending, in any letter case
		Check(Plugins.IsPlugin("Skyrim.ESM") && Plugins.IsPlugin("Patch.esl") && !Plugins.IsPlugin("Patch.esp.bak") && !Plugins.IsPlugin("esp"), "a plugin is known by its ending");
		// the download's name as GitHub lists it: its spaces made dots
		Check(ProgramUpdate.SameAssetName("Tool.Runner.1.1.zip", "Tool Runner 1.1.zip"), "GitHub's dots for spaces");
		Check(!ProgramUpdate.SameAssetName(null, "Tool Runner 1.1.zip") && !ProgramUpdate.SameAssetName("Tool.Runner.1.2.zip", "Tool Runner 1.1.zip"),
			"no name listed, or another version's");
		Equal(BoxMove.Name, KnownBoxes.Judge(new BoxSeen("TexGenx64.exe", false, "TexGen", "Exit TexGen?", ["Yes", "No"]), true).Move, "a tool's own box is only named, whatever the setup says about the game's");

		pressed.Clear();
		shown.Clear();
		using (var w = Watch()) {
			open.Add(Box(2, "SkyrimSE.exe", true, "Some Mod", "Reset every setting?", "Yes", "No"));
			w.LookOnce(t0);
			Equal(0, pressed.Count, "a question nobody here knows is never answered for the player");
			Check(shown.Count == 1 && shown[0].Contains("Reset every setting?", StringComparison.Ordinal) && shown[0].Contains("60 seconds", StringComparison.Ordinal), "the window says it and how long it has: " + string.Join(" | ", shown));
			w.LookOnce(t0.AddSeconds(59));
			Equal(null, w.Blocked, "inside the wait a player may still answer it");
			w.LookOnce(t0.AddSeconds(61));
			Check(w.Blocked is { } b && b.Contains("Reset every setting?", StringComparison.Ordinal) && b.Contains("[Yes / No]", StringComparison.Ordinal) && b.Contains("run again from this step", StringComparison.Ordinal),
				"after it the step stops with the box's own words: " + w.Blocked);
			Equal(1, ended, "and its game is ended");
			w.LookOnce(t0.AddSeconds(70));
			Equal(1, ended, "once");
			open.Clear();
		}

		pressed.Clear();
		shown.Clear();
		ended = 0;
		using (var w = Watch()) {
			open.Add(Box(3, "DynDOLODx64.exe", false, "DynDOLOD", "Something to decide", "Yes", "No"));
			open.Add(Box(4, "FasterArchivesTool.exe", true, "Faster Archives", "Faster Archives has detected unconverted archives", "Repack now", "Cancel"));
			open.Add(Box(5, "skse64_loader.exe", true, "SKSE Plugin Loader", "A DLL plugin has failed to load correctly. Continue loading?", "Yes", "No"));
			open.Add(Box(6, "SkyrimSE.exe", true, "Notice", "Your settings were reset.", "OK"));
			w.LookOnce(t0);
			Equal("5:No,6:OK", string.Join(",", pressed), "SKSE's plugin question gets No, a one-button notice is acknowledged, the others are not pressed");
			Check(w.GameSaidWithin(TimeSpan.FromMinutes(2), t0.AddSeconds(30))?.Contains("Your settings were reset.", StringComparison.Ordinal) == true,
				"what the game last said in a box is kept for a step that finds it gone: " + w.GameSaidWithin(TimeSpan.FromMinutes(2), t0.AddSeconds(30)));
			Equal(null, w.GameSaidWithin(TimeSpan.FromMinutes(2), t0.AddMinutes(5)), "but not for a close long after it");
			open.RemoveAll(o => o.Id is 5 or 6);
			w.LookOnce(t0.AddMinutes(30));
			Equal(2, pressed.Count, "a tool's own box and Faster Archives' window are never pressed");
			Equal(null, w.Blocked, "nor a reason to stop: the step that runs the tool, and the player, answer those");
			Equal(0, ended, "no game is ended over them");
			Check(shown[^1].Contains("DynDOLODx64.exe", StringComparison.Ordinal) && shown[^1].Contains(FasterArchives.Asks, StringComparison.Ordinal), "both are said in the window: " + shown[^1]);
			open.Clear();
			w.LookOnce(t0.AddMinutes(31));
			Equal("", shown[^1], "and no longer once they closed");
		}

		// a box of the game whose one button is Cancel (something is going on behind it): never pressed, and the step stops on it
		pressed.Clear();
		ended = 0;
		using (var w = Watch()) {
			open.Add(Box(8, "SkyrimSE.exe", true, "Working", "Rebuilding the cache, please wait.", "Cancel"));
			w.LookOnce(t0);
			w.LookOnce(t0.AddSeconds(61));
			Equal(0, pressed.Count, "a lone Cancel is not pressed");
			Check(w.Blocked?.Contains("Rebuilding the cache", StringComparison.Ordinal) == true && ended == 1, "the step stops with the box's words: " + w.Blocked);
			open.Clear();
		}

		// the watch on its own thread: a box that opens is answered within its beat, and it stops when the run ends
		var every = BoxWatch.Every;
		var endWait = BoxWatch.EndWait;
		BoxWatch.Every = TimeSpan.FromMilliseconds(40);
		BoxWatch.EndWait = TimeSpan.FromMilliseconds(300);
		try {
			// the run ends while a look is still out reading a window that does not answer: the end does not wait for it, and
			// when that look comes back it presses nothing and ends no game
			using var hung = new ManualResetEventSlim(false);
			using var isOut = new ManualResetEventSlim(false);
			var late = 0;
			var stuck = new BoxWatch(() => {
				isOut.Set();
				hung.Wait(TimeSpan.FromSeconds(30));
				return [new BoxWatch.Open(9, new BoxSeen("SkyrimSE.exe", true, "Notice", "Your settings were reset.", ["OK"]), _ => Interlocked.Increment(ref late))];
			}, log, _ => Interlocked.Increment(ref late), () => Interlocked.Increment(ref late));
			stuck.Start();
			Check(isOut.Wait(TimeSpan.FromSeconds(5)), "the look is out");
			var ending = Stopwatch.StartNew();
			stuck.Dispose();
			Check(ending.Elapsed < TimeSpan.FromSeconds(5), "the run's end does not wait for a look that sits in a hung window: " + ending.Elapsed);
			hung.Set();
			Check(stuck.Ended(TimeSpan.FromSeconds(20)), "the look has come back and the watch's thread has ended");
			Equal(0, late, "a look that comes back after the run let go of the watch presses nothing, ends no game and tells the window nothing");

			// ... and a look held up INSIDE its acting (a log line the disk holds up) while the run ends: the end does not
			// wait for it for ever, and when the look goes on, no press and no game's end begins any more
			using var release = new ManualResetEventSlim(false);
			using var inLog = new ManualResetEventSlim(false);
			using var slowLog = new RunLog(Path.Combine(f.Data, "box log held up"));
			slowLog.Line += (t, _) => {
				if (t.StartsWith("a box is open", StringComparison.Ordinal) && !inLog.IsSet) {
					inLog.Set();
					release.Wait(TimeSpan.FromSeconds(30));
				}
			};
			var lateActs = 0;
			var heldUp = new BoxWatch(() => [
				new BoxWatch.Open(10, new BoxSeen("SkyrimSE.exe", true, "Notice", "Your settings were reset.", ["OK"]), _ => Interlocked.Increment(ref lateActs)),
				new BoxWatch.Open(11, new BoxSeen("SkyrimSE.exe", true, "Some Mod", "Reset every setting?", ["Yes", "No"]), _ => Interlocked.Increment(ref lateActs))],
				slowLog, _ => Interlocked.Increment(ref lateActs), () => Interlocked.Increment(ref lateActs)) { Wait = TimeSpan.Zero };
			heldUp.Start();
			Check(inLog.Wait(TimeSpan.FromSeconds(5)), "the look is held up in its log line");
			var letGo = Stopwatch.StartNew();
			heldUp.Dispose();
			Check(letGo.Elapsed < TimeSpan.FromSeconds(5), "the run's end does not wait for it for ever: " + letGo.Elapsed);
			release.Set();
			Check(heldUp.Ended(TimeSpan.FromSeconds(20)), "the look has gone on and the watch's thread has ended");
			Equal(0, lateActs, "held up inside its acting while the run ended: no press, no game's end and no line for the window begins after");

			// a game the player started under the run's Mod Organizer is theirs with everything it starts
			Check(RunWindows.GameFlag(true, 5, pid => pid == 5) && !RunWindows.GameFlag(true, 5, pid => pid == 6), "a program under the run's own game is the game's; under the player's game it is not");
			Check(RunWindows.GameFlag(true, null, _ => false) && !RunWindows.GameFlag(false, 5, _ => true) && RunWindows.GameFlag(true, 5, null), "one under no game keeps its own flag, and none is made up");

			pressed.Clear();
			var live = new List<BoxWatch.Open>();
			using var w = new BoxWatch(() => {
				lock (live) {
					return live.ToList();
				}
			}, log, _ => { }, null);
			w.Start();
			lock (live) {
				live.Add(Box(7, "SkyrimSE.exe", true, "Cache Recursive Directory Walk", "Do you want to generate CRDW cache files?", "Yes", "No"));
			}
			var sw = Stopwatch.StartNew();
			while (sw.Elapsed < TimeSpan.FromSeconds(5)) {
				lock (pressed) {
					if (pressed.Count > 0) {
						break;
					}
				}
				Thread.Sleep(20);
			}
			Equal("7:Yes", pressed.FirstOrDefault(), "answered by the running watch");
		} finally {
			BoxWatch.Every = every;
			BoxWatch.EndWait = endWait;
		}
	}

	// Faster Archives closes the game itself for its offer (no crash), and Cached Recursive Directory Walk keeps a list of the
	// game's files that a run makes old: both are known by their own files, and a setting changed for a run goes back
	static void T_TheTwoModsThatStandInFrontOfAGameStart()
	{
		const string handOff = "[22:28:13.215] [67820] [info] handed off to D:\\Game\\data\\SKSE\\Plugins\\FasterArchives\\FasterArchivesTool.exe --dialog \"D:\\Game\\Data\" --mode mo2-in-place; closing the game until it is done";
		Check(FasterArchives.HandedOff("[22:28:12.638] [info] Faster Archives v1.1.0\n" + handOff), "its own last line says it closed the game");
		Check(!FasterArchives.HandedOff("[22:28:12.638] [info] Faster Archives v1.1.0\n[info] 2 archives to repack"), "a start that goes on is none");
		Check(FasterArchives.OfferOn("[Repack]\r\nbOfferRepack=1\r\n") && FasterArchives.OfferOn("[Repack]\r\n;bOfferRepack=0\r\n") && !FasterArchives.OfferOn("[Repack]\r\nbOfferRepack=0\r\n") &&
			!FasterArchives.OfferOn("[Repack]\nbOfferRepack = 0 ; off\n"), "whether it offers, off its own file");
		Check(FasterArchives.IsTool("FasterArchivesTool.exe") && !FasterArchives.IsTool("SkyrimSE.exe"), "its own program by its name");
		Check(InGame.IsCrashReport("crash-2026-11-07-23-07-14.log") && !InGame.IsCrashReport("CrashLogger.log") && !InGame.IsCrashReport("CrashLoggerSSE.log"),
			"a crash logger's own plugin log is no crash report (a game that only closed was once called crashed over it)");

		const string crdwIni = "; CRDW INI\r\n[General]\r\nShowPopUp=b:false\r\nMode=s:LOAD\r\nLogging=b:true\r\n";
		Equal((false, "LOAD"), Crdw.Parse(crdwIni), "its question off and LOAD behind it");
		Equal((true, "LOAD"), Crdw.Parse("[General]\r\nLogging=b:true\r\n"), "its own defaults for missing lines");
		Equal((false, "CACHE"), Crdw.Parse("[General]\nShowPopUp = b:false\nMode = s:cache\n"), "spaces and case");
		var blind = new Crdw.State("x", false, "LOAD");
		var asks = new Crdw.State("x", true, "LOAD");
		Equal(null, Crdw.AfterRun(null, true, false, false), "the mod is not there: nothing to say");
		Equal(null, Crdw.AfterRun(blind, false, false, false), "a run that built nothing made nothing old");
		Check(Crdw.AfterRun(blind, true, false, true)?.Contains("CACHE", StringComparison.Ordinal) == true, "it loads its old list without asking, and the game made none anew after the run's last change: said after the run");
		Equal(null, Crdw.AfterRun(blind, true, true, true), "the game made its list anew after the last thing the run changed: nothing left to do");
		Equal(null, Crdw.AfterRun(asks, true, false, true), "its question was answered Yes at the run's last game start");
		Check(Crdw.AfterRun(asks, true, false, false)?.Contains("answer Yes", StringComparison.Ordinal) == true, "no game start after the last tool: answer Yes next time");
		// a command of the setup ran after the game last made its list: whether it changed a game file nobody can tell from
		// here, so the list is never called current after it - the player is told what to do if it does
		Check(Crdw.AfterRun(blind, true, false, true, true)?.Contains("command after the run", StringComparison.Ordinal) == true, "a command after the run, after the game's last list: said, with what to do if it changes game files");
		Check(Crdw.AfterRun(blind, false, false, false, true)?.Contains("command after the run", StringComparison.Ordinal) == true, "also after a run that changed nothing itself");
		Check(Crdw.AfterRun(blind, true, false, false, false)?.Contains("older than what this run changed", StringComparison.Ordinal) == true, "a list the run itself left old is said as that, command or no command");
		Equal(null, Crdw.AfterRun(asks, true, false, true, true), "with its question on, the mod asks at the next game start itself");
		// when the run has it make a new list: a new list when files changed during the run is the rule for a setup that says
		// nothing
		Equal(Crdw.Renew.Changed, Crdw.When(Setup.FromJson(new JsonObject())), "a setup that says nothing: a new list when the run changed files");
		Equal(Crdw.Renew.Always, Crdw.When(Setup.FromJson(new JsonObject { [Crdw.SettingName] = "always" })), "at every game start, when the setup says so");
		Equal(Crdw.Renew.Never, Crdw.When(Setup.FromJson(new JsonObject { [Crdw.SettingName] = "Never" })), "or never");
		Equal(Crdw.Renew.Changed, Crdw.When(Setup.FromJson(new JsonObject { ["crdwCache"] = "leave" })), "the first builds' \"leave\" was written by every save of the settings, chosen or not: it says nothing");
		Equal(Crdw.Renew.Always, Crdw.When(Setup.FromJson(new JsonObject { ["crdwCache"] = "refresh" })), "their \"refresh\" had to be picked: it still counts");
		Equal(Crdw.Renew.Never, Crdw.When(Setup.FromJson(new JsonObject { ["crdwCache"] = "refresh", [Crdw.SettingName] = "never" })), "and what is picked now outranks it");
		// what the settings window shows (and so what a save writes) is what the run does today - never the list's first
		// choice for a key that is empty or holds a word the list does not have
		Equal("always", Crdw.Word(Setup.FromJson(new JsonObject { [Crdw.SettingName] = "", ["crdwCache"] = "refresh" })), "an empty value under the new key says nothing: the old key's \"refresh\" is shown, and a save keeps it");
		Equal("always", Crdw.Word(Setup.FromJson(new JsonObject { [Crdw.SettingName] = "  ", ["crdwCache"] = "refresh" })), "nor does one of blanks");
		Equal("never", Crdw.Word(Setup.FromJson(new JsonObject { [Crdw.SettingName] = "Never" })), "what was picked is shown in the list's own word");
		Equal("changed", Crdw.Word(Setup.FromJson(new JsonObject { [Crdw.SettingName] = "sometimes" })), "a word it does not know is shown as what the run does with it");
		Equal("changed", Crdw.Word(Setup.FromJson(new JsonObject())), "and a setup that says nothing as the rule for it");
		Check(Crdw.Writes("landscapefixer") && Crdw.Writes("grass") && !Crdw.Writes("cleansave") && !Crdw.Writes("playsave") && !Crdw.Writes("shaders"), "a step that only starts the game and saves changes no file the mod lists");

		using var f = new FakeMo2();
		f.Mod("CRDW Low");
		f.Mod("CRDW High");
		f.Mod("Faster Archives");
		var modlist = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(modlist, File.ReadAllText(modlist).Replace("+Base Mod", "+CRDW Low\r\n+CRDW High\r\n+Faster Archives\r\n+Base Mod", StringComparison.Ordinal));
		void Put(string a_mod, string a_rel, string a_text)
		{
			var p = Path.Combine(a_mod == "" ? f.Overwrite : Path.Combine(f.Mods, a_mod), a_rel);
			Directory.CreateDirectory(Path.GetDirectoryName(p)!);
			File.WriteAllText(p, a_text);
		}
		Put("CRDW Low", @"SKSE\Plugins\CRDW.dll", "x");
		Put("CRDW Low", Crdw.IniRel, crdwIni);
		Put("CRDW High", Crdw.IniRel, crdwIni.Replace("LOAD", "NATIVE", StringComparison.Ordinal));
		Put("Faster Archives", @"SKSE\Plugins\FasterArchives.dll", "x");
		Put("Faster Archives", FasterArchives.IniRel, "[General]\r\nbEnableParallel=1\r\n\r\n[Repack]\r\n; 1 = offer\r\nbOfferRepack=1\r\n");
		var setup = f.WriteSetup(("landscapefixer", []));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var profile = new Profile(mo2, "Test", () => false);
		var order = profile.ModOrder();
		var winner = order.First(m => m.StartsWith("CRDW", StringComparison.Ordinal));
		Equal(Path.Combine(f.Mods, winner, Crdw.IniRel), GameStart.Winning(profile, Crdw.IniRel), "the copy the game reads: the mod that wins");
		Put("", Crdw.IniRel, crdwIni);
		Equal(Path.Combine(f.Overwrite, Crdw.IniRel), GameStart.Winning(profile, Crdw.IniRel), "overwrite outranks every mod");
		Check(Crdw.Read(profile) is { PopUp: false, Mode: "LOAD" }, "read as the game reads it");

		// this PC's own SKSE logs are not this test's business
		setup.Root["skseLogs"] = Path.Combine(f.Root, "no logs here");
		setup.Save();
		var notes = Preflight.Run(setup, mo2, ["landscapefixer"]);
		Check(notes.Any(i => !i.Blocking && i.Text.Contains("Cached Recursive Directory Walk loads its cache without asking", StringComparison.Ordinal)), "the check says its list will be old: " + string.Join(" | ", notes.Select(i => i.Text)));
		Check(notes.Any(i => !i.Blocking && i.Text.StartsWith("Faster Archives is ticked", StringComparison.Ordinal)), "and that Faster Archives may ask at a game start");
		// the family of the run's processes: a Skyrim the run did not start is never the run's game
		var fam = new RunWindows.Family();
		var me = Environment.ProcessId;
		Check(fam.Now([me]).ContainsKey(me) && !fam.Now([me])[me].Game, "a process that is no game is none");

		// a run that has changed nothing yet touches neither mod's own settings; once it has changed the game's files, the
		// cache mod makes a new list at the next step's game starts, and its own file goes back after that step
		var crdwOwn = Path.Combine(f.Overwrite, Crdw.IniRel);
		var crdwAsWas = File.ReadAllBytes(crdwOwn);
		var ctx = StepContext(f, "landscapefixer");
		using (ctx.Log) {
			GameStart.Prepare(ctx);
			Equal(0, ctx.Journal.Kept.Count, "a run that has changed nothing yet changes neither mod's own settings");
			Crdw.StepEnded(ctx, true, true);
			Equal(1, ctx.Journal.FilesChanged, "a step that writes files ended: the game's files have changed");
			GameStart.Prepare(ctx);
			Equal((false, "CACHE"), Crdw.Parse(File.ReadAllText(crdwOwn)), "the next game start makes the list anew");
			Equal(1, ctx.Journal.Kept.Count, "only the cache mod's file is touched for it");
			Crdw.StepEnded(ctx, false, false);
			Equal(1, ctx.Journal.FilesChanged, "a game step that did not finish leaves the list as old as it was");
			Equal(0, StepKit.PutFilesBack(ctx).Count, "its file goes back after the failed step");
			GameStart.Prepare(ctx);
			GameStart.Prepare(ctx);
			Equal((false, "CACHE"), Crdw.Parse(File.ReadAllText(crdwOwn)), "set again for the next step, and a second game start of that step changes nothing more");
			Crdw.StepEnded(ctx, true, false);
			Equal(0, ctx.Journal.FilesChanged, "a game step that finished with a new list: the list is current");
			Equal(0, StepKit.PutFilesBack(ctx).Count, "the mod's own file goes back when the step ends");
			Check(File.ReadAllBytes(crdwOwn).SequenceEqual(crdwAsWas), "byte for byte as the player had it");
			GameStart.Prepare(ctx);
			Equal((false, "LOAD"), Crdw.Parse(File.ReadAllText(crdwOwn)), "nothing changed since: the next game start leaves the mod as the player set it");
			// a step that starts the game with a new list and then writes files itself (the grass cache): changed again
			ctx.Journal.FilesChanged = 1;
			GameStart.Prepare(ctx);
			Crdw.StepEnded(ctx, true, true);
			Equal(1, ctx.Journal.FilesChanged, "what a step wrote after its own game start counts as changed again");
			Equal(0, StepKit.PutFilesBack(ctx).Count, "and its file goes back as well");
			// A game step that switches a mod off for its game start: the list the game makes
			// then is of the mods as they are switched. With the mod back, that list is old again - also when the step itself
			// writes nothing, where it once stayed "current" and no later game start made a new one
			ctx.Journal.FilesChanged = 0;
			using (new Toggle(ctx, [], ["Faster Archives"], "the test")) {
				Equal(1, ctx.Journal.FilesChanged, "mods switched for a step: the game would find other files than its list names");
				GameStart.Prepare(ctx);
				Equal((false, "CACHE"), Crdw.Parse(File.ReadAllText(crdwOwn)), "so this step's game start makes the list anew");
			}
			Crdw.StepEnded(ctx, true, false);
			Equal(1, ctx.Journal.FilesChanged, "the list was made with the mod switched off: with the mod back it is old again, though the step wrote nothing");
			Equal(0, StepKit.PutFilesBack(ctx).Count, "(the mod's own file back)");
			GameStart.Prepare(ctx);
			Equal((false, "CACHE"), Crdw.Parse(File.ReadAllText(crdwOwn)), "and the next game start makes a new one");
			Crdw.StepEnded(ctx, true, false);
			Equal(0, ctx.Journal.FilesChanged, "which is current: nothing was switched for that start");
			Equal(0, StepKit.PutFilesBack(ctx).Count, "(and back again)");
			// mods switched for a tool and put back, with no game start in between, change nothing for the game's list
			using (new Toggle(ctx, [], ["Faster Archives"], "the test")) {
			}
			Equal(0, ctx.Journal.FilesChanged, "switched and put back with no list made meanwhile: as it was");
			// a step that asks for a mod as it already stands switches nothing: the game's files are what its list names
			using (new Toggle(ctx, ["Faster Archives"], [], "the test")) {
				Equal(0, ctx.Journal.FilesChanged, "a mod asked on that is on already: nothing switched, nothing changed for the game's list");
				ctx.FreshList = true;
			}
			Check(!ctx.SwitchedBack, "and with nothing switched there is nothing to put back after the list was made");
			ctx.FreshList = false;
			// The final save runs after the run's record says finished. A file kept for it is on record as "to put back" in
			// the very write that records the file - before the file is changed (said only after the change, a crash in
			// between left a changed file that nothing offered to put back)
			var forPlay = Path.Combine(f.Root, "changed for the final save.ini");
			File.WriteAllText(forPlay, "as it was");
			ctx.Journal.InGameAfter = true;
			ctx.Journal.NeedsRestore = false;
			StepKit.KeepFile(ctx, forPlay);
			var onDisk = Journal.Load(ctx.Journal.Folder);
			Check(onDisk.NeedsRestore && onDisk.Unfinished && onDisk.Kept.Any(k => k.Path == forPlay && !k.Done), "the record on disk says there is something to put back, with the file named");
			Equal("as it was", File.ReadAllText(forPlay), "before the file itself is touched");
			Equal(0, StepKit.PutFilesBack(ctx).Count, "(put back)");
			ctx.Journal.InGameAfter = false;
			ctx.Journal.NeedsRestore = false;
		}
		setup.Root[Crdw.SettingName] = "never";
		setup.Save();
		ctx = StepContext(f, "landscapefixer");
		using (ctx.Log) {
			ctx.Journal.FilesChanged = 3;
			GameStart.Prepare(ctx);
			Equal(0, ctx.Journal.Kept.Count, "a setup that says never: the mod is left alone whatever the run changed");
		}
		setup.Root[Crdw.SettingName] = "always";
		setup.Root[FasterArchives.SettingName] = "skip";
		setup.Save();
		Check(!Preflight.Run(Setup.Load(f.SetupFile), mo2, ["landscapefixer"]).Any(i => i.Text.StartsWith("Faster Archives is ticked", StringComparison.Ordinal)), "a setup that passes the offer over is not warned of it");
		var crdwFile = Path.Combine(f.Overwrite, Crdw.IniRel);
		var faFile = Path.Combine(f.Mods, "Faster Archives", FasterArchives.IniRel);
		var crdwBefore = File.ReadAllBytes(crdwFile);
		var faBefore = File.ReadAllBytes(faFile);
		ctx = StepContext(f, "landscapefixer");
		using (ctx.Log) {
			GameStart.Prepare(ctx);
			Equal((false, "CACHE"), Crdw.Parse(File.ReadAllText(crdwFile)), "always: it makes its list anew at this step's game starts, whether or not anything changed");
			Check(!FasterArchives.OfferOn(File.ReadAllText(faFile)), "skip: no offer at this step's game starts");
			Check(File.ReadAllText(faFile).Contains("bEnableParallel=1", StringComparison.Ordinal) && File.ReadAllText(faFile).Contains("; 1 = offer", StringComparison.Ordinal), "every other line of the file as it was");
			GameStart.Prepare(ctx);
			Equal(2, ctx.Journal.Kept.Count, "a second game start of the step keeps nothing twice");
			Equal(0, StepKit.PutFilesBack(ctx).Count, "both files go back when the step ends");
			Check(File.ReadAllBytes(crdwFile).SequenceEqual(crdwBefore) && File.ReadAllBytes(faFile).SequenceEqual(faBefore), "byte for byte as the player had them");
		}

		// the game closed for the offer: known by the mod's own log of that start
		var logs = Path.Combine(f.Root, "skse logs");
		Directory.CreateDirectory(logs);
		File.WriteAllText(Path.Combine(logs, "FasterArchives.log"), "[info] Faster Archives v1.1.0\n" + handOff + "\n");
		Check(FasterArchives.Offered([logs], DateTime.Now.AddSeconds(-30), new StartedSet()), "its log of this start says it closed the game");
		Check(!FasterArchives.Offered([logs], DateTime.Now.AddMinutes(10), new StartedSet()), "a log from before this start says nothing about it");
		// with nothing left to repack by its own last log, the check stays quiet about it
		setup.Root["skseLogs"] = logs;
		setup.Root.Remove(FasterArchives.SettingName);
		setup.Save();
		Check(Preflight.Run(Setup.Load(f.SetupFile), mo2, ["landscapefixer"]).Any(i => i.Text.StartsWith("Faster Archives is ticked", StringComparison.Ordinal)), "archives left to repack: said");
		File.WriteAllText(Path.Combine(logs, "FasterArchives.log"), "[22:28:13.209] [67820] [info] 0 archives to repack (0 into the Vortex mod), 67 already repacked\n");
		Check(!Preflight.Run(Setup.Load(f.SetupFile), mo2, ["landscapefixer"]).Any(i => i.Text.StartsWith("Faster Archives is ticked", StringComparison.Ordinal)), "nothing left: not said");
	}

	// A stand-in program of a test, ended when the test lets go of it - also when a check fails first. One left running keeps
	// the PC busy and holds open whatever it inherited from the tests (a script that read the tests' output through a pipe
	// waited an hour on one)
	sealed class StandIn(Process a_p) : IDisposable
	{
		public void Dispose()
		{
			try {
				if (!a_p.HasExited) {
					a_p.Kill(true);
				}
			} catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException or NotSupportedException) {
			}
		}
	}

	// the game closed for Faster Archives' offer: its own program is what the run waits for (ping stands in for it, under its name)
	static void T_FasterArchivesOfferIsWaitedOutNeverCalledACrash()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("shaders", []));
		var ctx = StepContext(f, "shaders");
		using var log = ctx.Log;
		var said = new List<string>();
		ctx.Control.WaitingForPlayer = said.Add;
		var tool = Path.Combine(f.Root, FasterArchives.ToolImage);
		File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), tool);
		// in a run its program is the game's child, and the game the run's own: here this test stands in for the game
		ctx.Control.Started.Add(Environment.ProcessId);
		var since = DateTime.Now;
		using var p = Process.Start(new ProcessStartInfo(tool, "-n 4 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
		using var endP = new StandIn(p);
		Check(FasterArchives.Offered([], since, ctx.Control.Started), "its own program runs since this game start: the game was closed for its offer");
		FasterArchives.Wait(ctx, "shaders", new StepSettings([]), 1, since);
		Check(p.HasExited, "the wait ends when its window has closed");
		Check(said.Count == 2 && said[0] == FasterArchives.Asks && said[1] == "", "the window says the run waits for the player, then no longer: " + string.Join(" | ", said));
		Check(!FasterArchives.Offered([], DateTime.Now.AddSeconds(5), ctx.Control.Started), "an old window says nothing about a later start");
		Throws<StepFailedException>(() => FasterArchives.Wait(ctx, "shaders", new StepSettings([]), 4, since), "an offer more often than the step allows stops it, in words");

		// one this run did not start (another game of the player's opened it in the same seconds) is not the run's to wait for,
		// and never ended by it
		var noGame = StepContext(f, "shaders");
		using var log0 = noGame.Log;
		var since0 = DateTime.Now;
		using var theirs = Process.Start(new ProcessStartInfo(tool, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
		using var endTheirs = new StandIn(theirs);
		Check(!FasterArchives.Offered([], since0, noGame.Control.Started), "a window of that program the run did not start says nothing about the run's game");
		Throws<StepFailedException>(() => FasterArchives.Wait(noGame, "shaders", new StepSettings([]), 4, since0), "the offer limit still stops the step");
		Check(!theirs.HasExited, "and the window the run did not start is left open");
		theirs.Kill();

		// at the offer limit a window that is repacking by then is never cut off: it is waited out (a busy program stands in)
		var look = FasterArchives.OfferLook;
		FasterArchives.OfferLook = TimeSpan.FromMilliseconds(500);
		try {
			var busyDir = Path.Combine(f.Root, "busy");
			Directory.CreateDirectory(busyDir);
			var busyTool = Path.Combine(busyDir, FasterArchives.ToolImage);
			File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), busyTool);
			var lines = new List<string>();
			var sinceBusy = DateTime.Now;
			// (a loop that ends by itself after a few minutes at most, whatever becomes of this test)
			using var busy = Process.Start(new ProcessStartInfo(busyTool, "/d /c for /l %i in (1,1,200000000) do @rem") { UseShellExecute = false, CreateNoWindow = true })!;
			using var endBusy = new StandIn(busy);
			// the tests may run at the lowest priority beside a busy PC: the stand-in must get its share to count as working
			busy.PriorityClass = ProcessPriorityClass.Normal;
			var aliveWhenEnded = false;
			// it ends "by itself" a second after the run has said that it is repacking (a clock of its own could run out
			// before a slow PC got that far). Nothing may leave this callback: an error on its thread would end the tests
			using var ender = new Timer(_ => {
				try {
					aliveWhenEnded = !busy.HasExited;
					busy.Kill();
				} catch (Exception e) when (e is not OutOfMemoryException) {
				}
			}, null, Timeout.Infinite, Timeout.Infinite);
			ctx.Log.Line += (t, _) => {
				lock (lines) {
					lines.Add(t);
				}
				if (t.Contains("this time it is repacking", StringComparison.Ordinal)) {
					ender.Change(1000, Timeout.Infinite);
				}
			};
			FasterArchives.Wait(ctx, "shaders", new StepSettings([]), 4, sinceBusy);
			lock (lines) {
				Check(lines.Any(l => l.Contains("this time it is repacking", StringComparison.Ordinal)), "past the limit, a program that works is said to be repacking: " + string.Join(" | ", lines.TakeLast(4)));
			}
			Check(aliveWhenEnded, "and was never cut off: it ran until it ended by itself");

			// ... and one that only sits there past the limit is LEFT OPEN, the run's own too: whether the player is choosing
			// "Repack now" in it this very moment cannot be told for certain, so a late choice is never cut off
			var sinceLate = DateTime.Now;
			using var late = Process.Start(new ProcessStartInfo(tool, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
			using var endLate = new StandIn(late);
			string? atLimit = null;
			try {
				FasterArchives.Wait(ctx, "shaders", new StepSettings([]), 4, sinceLate);
			} catch (StepFailedException e) {
				atLimit = e.Message;
			}
			Check(atLimit?.Contains("left open", StringComparison.Ordinal) == true && !late.HasExited, "past the limit the step stops and its window stays open: " + atLimit);
			late.Kill();
		} finally {
			FasterArchives.OfferLook = look;
		}

		// nobody chooses: a window that only waits is closed, as its Cancel would, and the step stops in words
		var since2 = DateTime.Now;
		using var idle = Process.Start(new ProcessStartInfo(tool, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
		using var endIdle = new StandIn(idle);
		string? gaveUp = null;
		try {
			FasterArchives.Wait(ctx, "shaders", new StepSettings(new JsonObject { ["offer_wait_min"] = 0.02 }), 1, since2);
		} catch (StepFailedException e) {
			gaveUp = e.Message;
		}
		Check(gaveUp?.Contains("nobody chose", StringComparison.Ordinal) == true, "nobody chose within the wait: the step stops and says so: " + gaveUp);
		Check(idle.WaitForExit(5000), "and the window that only waited is closed, so Mod Organizer can close");

		// a Stop never closes its window, the run's own too: whether it only waits or a repack began a moment ago cannot be
		// told for certain from outside, and a repack cut off leaves archives half written
		var since3 = DateTime.Now;
		using var stopMe = Process.Start(new ProcessStartInfo(tool, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
		using var endStopMe = new StandIn(stopMe);
		using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
		var stopping = StepContext(f, "shaders", a_cancel: stop.Token);
		stopping.Control.Started.Add(Environment.ProcessId);
		using var log2 = stopping.Log;
		var atStop = new List<string>();
		stopping.Log.Line += (t, _) => {
			lock (atStop) {
				atStop.Add(t);
			}
		};
		Throws<OperationCanceledException>(() => FasterArchives.Wait(stopping, "shaders", new StepSettings([]), 1, since3), "a Stop ends the wait");
		Check(!stopMe.WaitForExit(1500), "and leaves its window open, one that only waits too");
		lock (atStop) {
			Check(atStop.Any(l => l.Contains(FasterArchives.LeftAtStop, StringComparison.Ordinal)), "said in words, with what to do: " + string.Join(" | ", atStop.TakeLast(3)));
		}
		stopMe.Kill();

		// ... and the choice at the edge: a window that sits idle up to the Stop and starts to work right after it (the
		// player chose "Repack now" as Stop was pressed) is not cut off, at the offer limit either
		var edgeDir = Path.Combine(f.Root, "edge");
		Directory.CreateDirectory(edgeDir);
		var edgeTool = Path.Combine(edgeDir, FasterArchives.ToolImage);
		File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), edgeTool);
		foreach (var time in new[] { 1, 4 }) {
			var sinceEdge = DateTime.Now;
			// idle for about a second, then busy (a loop that ends by itself after a few minutes at most)
			using var edge = Process.Start(new ProcessStartInfo(edgeTool, "/d /c \"ping -n 2 127.0.0.1 >nul & for /l %i in (1,1,200000000) do @rem\"") { UseShellExecute = false, CreateNoWindow = true })!;
			using var endEdge = new StandIn(edge);
			using var stopEdge = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
			var edgeCtx = StepContext(f, "shaders", a_cancel: stopEdge.Token);
			edgeCtx.Control.Started.Add(Environment.ProcessId);
			using var edgeLog = edgeCtx.Log;
			Throws<OperationCanceledException>(() => FasterArchives.Wait(edgeCtx, "shaders", new StepSettings([]), time, sinceEdge), $"a Stop ends the wait (offer {time})");
			Check(!edge.WaitForExit(2000), $"a window that began to work as the Stop came is left to it (offer {time})");
		}
	}

	// StartAndLoad's own loop around Faster Archives' offer. The game closes a few seconds into its
	// start for the offer (its program runs, the game does not): never taken for a crash - the run waits for that window, then
	// starts the game again. A stand-in starts the "game" (ping under the offer program's name, this test standing in for the
	// game); DevBench never answers here (a port nothing listens on), and the second start stops the step
	static void T_AGameClosedForTheRepackOfferIsStartedAgain()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("shaders", []));
		var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		probe.Start();
		var dead = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		using var cts = new CancellationTokenSource();
		var ctx = StepContext(f, "shaders", new DevBench($"http://127.0.0.1:{dead}/api/tool/"), cts.Token);
		using var log = ctx.Log;
		var tool = Path.Combine(f.Root, FasterArchives.ToolImage);
		File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), tool);
		var lines = new List<string>();
		ctx.Log.Line += (t, _) => {
			lock (lines) {
				lines.Add(t);
			}
		};
		var launches = 0;
		Process? offer = null;
		var offerGoneAtSecond = false;
		InGame.LaunchStandIn = (c, _) => {
			if (++launches == 1) {
				// its program outlives the start loop's first looks for an offer (each ask of a DevBench that is not there can
				// take a few seconds to be refused), then closes by itself
				c.Control.Started.Add(Environment.ProcessId);
				offer = Process.Start(new ProcessStartInfo(tool, "-n 20 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true });
			} else {
				offerGoneAtSecond = offer?.HasExited == true;
				cts.Cancel();
			}
		};
		try {
			Throws<OperationCanceledException>(() => InGame.StartAndLoad(ctx, "shaders", new StepSettings([]), "Some Save"), "(the second start stops the step)");
		} finally {
			InGame.LaunchStandIn = null;
			try {
				offer?.Kill();
			} catch (InvalidOperationException) {
			}
			offer?.Dispose();
		}
		Equal(2, launches, "the game was started again after the offer");
		Check(offerGoneAtSecond, "only once the offer's window had closed");
		lock (lines) {
			Check(lines.All(l => !l.Contains("while it was starting", StringComparison.Ordinal)), "and the close was never taken for a crash: " + string.Join(" | ", lines.TakeLast(5)));
		}
	}

	// work cut off part way (a park, a put-back, a file written in place) loses nothing, and a few small rules of the same kind
	static void T_WorkCutOffPartWayLosesNothing()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", []));
		var ctx = StepContext(f, "landscapefixer");
		using var log = ctx.Log;
		var mod = Path.Combine(f.Mods, "Output A");

		// a park cut off part way: what still stands in the mod is the rest of the player's old output, never a tool's
		File.WriteAllText(Path.Combine(mod, "old one.txt"), "1");
		File.WriteAllText(Path.Combine(mod, "old two.txt"), "2");
		StepKit.ParkMod(ctx, "landscapefixer", "Output A");
		var park = ctx.Journal.Parked[^1];
		Check(!park.Parking && File.Exists(Path.Combine(park.To, "old two.txt")), "a park that ended is on record as ended, the files in the backup");
		File.Move(Path.Combine(park.To, "old two.txt"), Path.Combine(mod, "old two.txt"));
		park.Parking = true;
		Equal(0, Runner.PutBackParked(ctx, "landscapefixer").Count, "the put-back after a park that was cut off");
		Check(File.Exists(Path.Combine(mod, "old one.txt")) && File.Exists(Path.Combine(mod, "old two.txt")), "both of the player's old files are in the mod again");
		Check(!Directory.Exists(Path.Combine(ctx.BackupFolder, "unfinished", "Output A")), "none of them was taken for half-written output");

		// a put-back cut off part way: what is back in the mod already stays there at the next try
		StepKit.ParkMod(ctx, "landscapefixer", "Output A");
		var again = ctx.Journal.Parked[^1];
		File.Move(Path.Combine(again.To, "old one.txt"), Path.Combine(mod, "old one.txt"));
		again.Returning = true;
		Equal(0, Runner.PutBackParked(ctx, "landscapefixer").Count, "the put-back tried again");
		Check(File.Exists(Path.Combine(mod, "old one.txt")) && File.Exists(Path.Combine(mod, "old two.txt")) && again.PutBack, "all of the old output is in the mod");
		Check(!Directory.Exists(Path.Combine(ctx.BackupFolder, "unfinished", "Output A")), "what was back already was not taken for half-written output");
		// ... and cut off after the last file had moved, before the record could say so
		StepKit.ParkMod(ctx, "landscapefixer", "Output A");
		var last = ctx.Journal.Parked[^1];
		Files.MoveContents(last.To, last.From);
		Directory.Delete(last.To, true);
		last.Returning = true;
		Equal(0, Runner.PutBackParked(ctx, "landscapefixer").Count, "nothing left to move");
		Check(last.PutBack && !last.Lost && File.Exists(Path.Combine(mod, "old two.txt")), "put back, never written off as lost");

		// two steps that wrote the same file in place: an undo goes back to what stood there before the first
		var twice = new List<KeptFile> { new() { Path = @"X:\mod\patch.esp", Backup = null, Settled = true }, new() { Path = @"x:\MOD\patch.esp", Backup = "the first step's output", Settled = true },
			new() { Path = @"X:\mod\other.esp", Backup = "its own old bytes", Settled = true } };
		var firsts = RunUndo.FirstStates(twice);
		Check(firsts.Count == 2 && firsts[0].Backup == null && firsts[1].Backup == "its own old bytes", "each file's first record is the state to go back to");

		// a list of processes Windows would not give is no proof that Mod Organizer is closed, even when the look after it works
		var open = new Mo2Control(ctx.Mo2, "Test", RunLog.Null);
		Check(!open.Running, "no Mod Organizer of this instance runs");
		Procs.FailLooks = 1;
		Check(open.Running, "one look failed: it counts as open");
		Check(!open.Running, "and as closed again once Windows answers");

		Equal("Theme CON.demo copy", ToolRunner.App.Theme.FileSafe("CON.demo copy"), "a name Windows keeps for a device gets a word in front");
		Check(ToolRunner.App.Theme.PlainFont("Segoe UI Variable Text, Segoe UI"), "a font's family names are a font");
		foreach (var place in new[] { @"\\server\share\#Font", "file:///C:/fonts/#Font", "./#Font", @"C:\fonts\Font.ttf", " " }) {
			Check(!ToolRunner.App.Theme.PlainFont(place), $"a theme's font is a name, never a place to load one from: {place}");
		}
		Equal("Ocean  dark copy", ToolRunner.App.Theme.FileSafe("Ocean: dark copy"), "a character Windows refuses in a name is taken out");
		Equal("Theme", ToolRunner.App.Theme.FileSafe("..."), "a name with nothing left is given one");

		// a file a tool writes new, straight into a mod: kept as "was not there", gone again when the step fails, and on record
		// as built in the same write that settles it
		var fresh = Path.Combine(mod, "New Patch.esp");
		StepKit.KeepInPlace(ctx, "Output A", "New Patch.esp");
		Check(ctx.Journal.Kept.Any(k => k.Path == fresh && k.Backup == null && !k.Done), "a file that is not there yet is kept as not there");
		File.WriteAllText(fresh, "half written");
		Equal(0, StepKit.PutFilesBack(ctx, fresh).Count, "the step failed: its files go back");
		Check(!File.Exists(fresh), "the half-written new file is taken out");
		StepKit.KeepInPlace(ctx, "Output A", "New Patch.esp");
		File.WriteAllText(fresh, "whole");
		ctx.Journal.Built.Clear();
		StepKit.Settled(ctx, "Output A", [fresh]);
		var saved = Journal.Load(ctx.Journal.Folder);
		Check(saved.Built.Contains("Output A") && saved.Kept.Any(k => k.Path == fresh && k.Settled && k.Done), "settled and on record as built, in one write");
		Equal(0, StepKit.PutFilesBack(ctx).Count, "the step's own clean-up");
		Check(File.Exists(fresh), "leaves the settled file where it is");

		// the folder a Mod Organizer plugin maps SKSE's logs to, made only later by the game
		var plugins = Path.Combine(f.Root, "mo2 plugins");
		Directory.CreateDirectory(plugins);
		var later = Path.Combine(f.Root, "redirected logs");
		File.WriteAllText(Path.Combine(plugins, "skse log redirector.py"), "class Redirect(mobase.IPluginFileMapper):\n    # SKSE logs\n    target = r\"" + later + "\"\n");
		Equal(0, InGame.MappedLogFolders(plugins).Count, "not there yet: not searched");
		Directory.CreateDirectory(later);
		File.WriteAllText(Path.Combine(later, "skse64.log"), "x");
		Equal(1, InGame.MappedLogFolders(plugins).Count, "there now: found, though the plugin's own file did not change");

		// the start-up pass of a shader mod, read out of its log in its folder (the call once handed the log's lines on as folders)
		var logs = Path.Combine(f.Root, "shader logs");
		Directory.CreateDirectory(logs);
		var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
		File.WriteAllText(Path.Combine(logs, "BottledShaders.log"), $"[{stamp}.123] [info] [DDC] 120 entries queued for trim\n");
		Check(InGame.BootPassOverIn([logs], DateTime.Now.AddSeconds(-5)), "the pass is over, by this start's own line");
		Check(!InGame.BootPassOverIn([logs], DateTime.Now.AddMinutes(5)), "a log from before this start says nothing");

		// a Learn cut off under ANOTHER setup of the same profile: this setup's start switches that mod off too
		var profile = ProfileOf(f);
		profile.SetMods(["Base Mod"], []);
		var setups = Path.GetDirectoryName(f.SetupFile)!;
		var others = Path.Combine(setups, "Other.json.tool on");
		var elsewhere = Path.Combine(setups, "Third.json.tool on");
		File.WriteAllText(others, ToolSettings.SwitchText("Base Mod", ctx.Mo2, "Test"));
		File.WriteAllText(elsewhere, ToolSettings.SwitchText("Base Mod", ctx.Mo2, "Another Profile"));
		ToolSettings.UndoLeftSwitch(ctx.Setup, profile, ctx.Log);
		Check(!profile.IsOn("Base Mod") && !File.Exists(others), "the mod another setup's Learn left on in this profile is off again, its record gone");
		Check(File.Exists(elsewhere), "a record for another profile is left for that profile");

		Check(RunnerErrors.All.Select(e => e.Code).Distinct().Count() == RunnerErrors.All.Count, "every catalog code is given once");

		// a failed step that started the game: a file of the player's in overwrite that the game rewrote stays, what is new goes
		var began = DateTime.UtcNow.AddMinutes(-10);
		var beforeOw = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { ["a mod's settings.json"] = began.AddDays(-3), ["untouched.ini"] = began.AddDays(-3) };
		string[] madeOw = ["a mod's settings.json", "half a cache.bin"];
		Equal("a mod's settings.json", string.Join(",", Runner.RewrittenByTheGame(madeOw, beforeOw, began.AddMinutes(1).ToLocalTime(), began)), "the game ran in this step: its rewrite of a file that was there stays");
		Equal("", string.Join(",", Runner.RewrittenByTheGame(madeOw, beforeOw, began.AddHours(-1).ToLocalTime(), began)), "the game last ran in an earlier step: a tool's rewrite goes with the rest");
		Equal("", string.Join(",", Runner.RewrittenByTheGame(madeOw, beforeOw, null, began)), "no game in this run: as before");

		// a Mod Organizer set up for another game is said before anything runs
		Equal(0, Preflight.Run(Setup.Load(f.SetupFile), Mo2Instance.Load(f.Exe, f.Instance), ["landscapefixer"]).Count(i => i.Text.Contains("another game", StringComparison.Ordinal)), "Skyrim Special Edition: nothing said");
		var ini = Path.Combine(f.Instance, "ModOrganizer.ini");
		File.WriteAllText(ini, File.ReadAllText(ini).Replace("gameName=Skyrim Special Edition", "gameName=Skyrim VR", StringComparison.Ordinal));
		var vr = Preflight.Run(Setup.Load(f.SetupFile), Mo2Instance.Load(f.Exe, f.Instance), ["landscapefixer"]);
		Check(vr.Any(i => i.Blocking && i.Text.Contains("Skyrim VR", StringComparison.Ordinal)), "Skyrim VR's Mod Organizer stops the start, named: " + string.Join(" | ", vr.Select(i => i.Text)));
		Check(Explain.For("", vr.First(i => i.Text.Contains("Skyrim VR", StringComparison.Ordinal)).Text)?.Code == "TR-213", "with its entry in the catalog");
	}

	// a new game has no save to keep clean - the clean save and the final save are left out while it is on
	static void T_NewGameLeavesOutTheSaveSteps()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("cleansave", []), ("landscapefixer", []), ("synthesis", []), ("playsave", []));
		Equal("cleansave,landscapefixer,synthesis,playsave", string.Join(",", Runner.Plan(s, null, null, null).Select(x => x.Key)), "an existing save: every step");
		s.Root["newGame"] = true;
		s.Save();
		var ng = Setup.Load(f.SetupFile);
		Check(ng.NewGame, "the tick is kept in the setup");
		Equal("landscapefixer,synthesis", string.Join(",", Runner.Plan(ng, null, null, null).Select(x => x.Key)), "a new game: the save steps left out");
		Equal("landscapefixer,synthesis", string.Join(",", ng.EnabledSteps.Select(x => x.Key)), "the check and the run see the same steps");
		ArgumentException? refusal = null;
		try {
			Runner.Plan(ng, "cleansave", null, null);
		} catch (ArgumentException e) {
			refusal = e;
		}
		Check(refusal?.Message.Contains("New game", StringComparison.Ordinal) == true, "only the clean save on a new game is refused, naming the tick: " + refusal?.Message);
		Equal(0, Preflight.Run(ng, Mo2Instance.Load(f.Exe, f.Instance), ng.EnabledSteps.Select(x => x.Key).ToList()).Count(i => i.Step is "cleansave" or "playsave"),
			"no save step asks for a save");
		var lines = RunPreview.Build(ng, Mo2Instance.Load(f.Exe, f.Instance), Runner.Plan(ng, null, null, null)).Lines(_ => null);
		Check(lines.Any(l => l.Contains("new game", StringComparison.Ordinal) && l.Contains("Clean save", StringComparison.Ordinal) && l.Contains("Final save", StringComparison.Ordinal)),
			"the preview says what is left out: " + string.Join(" | ", lines));
		Check(!lines.Any(l => l.StartsWith("The game starts", StringComparison.Ordinal)), "and that the game does not start");
		ng.Root["steps"]!.AsArray().OfType<JsonObject>().Where(o => Setup.Str(o, "step") is "landscapefixer" or "synthesis").ToList().ForEach(o => o["on"] = false);
		Check(ng.Problems().Any(p => p.Contains("new game", StringComparison.Ordinal)), "only save steps on, on a new game: said plainly");
	}

	static void T_SetupProblems()
	{
		var s = Setup.FromJson(new JsonObject { ["steps"] = new JsonArray(new JsonObject { ["step"] = "nonsense" }) });
		var p = s.Problems();
		Check(p.Any(x => x.Contains("Mod Organizer", StringComparison.Ordinal)), string.Join("; ", p));
		// a step this version does not have (an older version's, or a name never known) is left out, never a refusal: an older
		// setup file still loads and runs its other steps
		Equal("nonsense", string.Join(",", s.NotKnown), "named as not known");
		Check(s.OnSteps.Count == 0 && p.Any(x => x.StartsWith("none of the steps it has switched on is one this version has: nonsense", StringComparison.Ordinal)), "and left out of the run, which then has nothing to run: " + string.Join("; ", p));
		var mixed = Setup.FromJson(new JsonObject { ["steps"] = new JsonArray(new JsonObject { ["step"] = "nonsense" }, new JsonObject { ["step"] = "synthesis" }) });
		Equal("synthesis", string.Join(",", mixed.EnabledSteps.Select(x => x.Key)), "the steps it knows still run");
	}

	static void T_SetupSaveKeepsUnknownKeys()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("landscapefixer", new JsonObject { ["_note"] = "the player's note 🔁", ["custom"] = 3 }));
		var back = Setup.Load(f.SetupFile);
		Equal("the player's note 🔁", Setup.Str(back.StepSettings("landscapefixer")!, "_note"), "a note key kept");
		Equal(3, Setup.Int(back.StepSettings("landscapefixer")!, "custom"), "an unknown key kept");
		_ = s;
	}

	// ------------------------------------------------------------------ in-game signals
	static void T_LoadBoxDecisions()
	{
		Equal(("answered NO", 1), DevBench.DecideBox("Would you like to search the Creations menu for missing content?", ["Yes", "No"], 2), "search -> No");
		Equal(("answered YES", 0), DevBench.DecideBox("Some content is missing. Continue loading?", ["Yes", "No"], 2), "continue -> Yes");
		Equal(("acknowledged", 0), DevBench.DecideBox("Perks reset", ["Ok"], 1), "one button");
		Equal<string?>(null, DevBench.DecideBox("Delete your save?", ["Yes", "No"], 2).Answer, "an unknown question is not answered");
		// ... it is CLOSED (whatever the box is, it is closed at once) - by the button that takes nothing on
		Equal(1, DevBench.CloseIndex(["Yes", "No"], -1, 2), "a box the run does not know: the button that declines");
		Equal(2, DevBench.CloseIndex(["Enable", "Disable", "Ask me later"], 2, 3), "the button the game itself marks as the box's cancel comes first");
		Equal(0, DevBench.CloseIndex(["Cancel", "Delete everything"], -1, 2), "a declining button wherever it stands");
		Equal(1, DevBench.CloseIndex(["Install now", "Not now"], -1, 2), "one that puts it off");
		Equal(1, DevBench.CloseIndex(["Yes, reset", "No, keep them"], -1, 2), "one whose words begin by declining");
		Equal(2, DevBench.CloseIndex(["Abort", "Retry", "Ignore"], -1, 3), "Windows' own: Ignore");
		Equal(2, DevBench.CloseIndex(["Warrior", "Mage", "Thief"], -1, 3), "none declines: the last, where the way out stands");
		Equal(0, DevBench.CloseIndex(["Ok"], -1, 1), "its only button");
		Equal(1, DevBench.CloseIndex([], -1, 2), "buttons without words: the last of them");
		Equal(1, DevBench.CloseIndex(["Yes", "No"], 7, 2), "a cancel place the box does not have says nothing");
		// the shader mod's own word about its start-up pass (Open Shaders, through DevBench): over or not, and how much it compiled
		var whole = InGame.ReadShaderPass(JsonNode.Parse("""{"compiling":false,"totalTasks":3201,"completedTasks":3201,"diskHitTasks":3201,"failedTasks":0}""")!.AsObject())!;
		Check(whole.Over && whole.Compiled == 0 && InGame.ShaderPassSaid(whole).Contains("all 3201 taken from its cache, none compiled", StringComparison.Ordinal),
			"every shader from the cache: over, and nothing compiled - " + InGame.ShaderPassSaid(whole));
		var rebuilt = InGame.ReadShaderPass(JsonNode.Parse("""{"compiling":false,"totalTasks":3201,"completedTasks":3201,"diskHitTasks":1589}""")!.AsObject())!;
		Check(rebuilt.Over && rebuilt.Compiled == 1612 && InGame.ShaderPassSaid(rebuilt).Contains("1612 of 3201 compiled", StringComparison.Ordinal), "what did not come from the cache was compiled at this start");
		var going = InGame.ReadShaderPass(JsonNode.Parse("""{"compiling":true,"totalTasks":3201,"completedTasks":40,"diskHitTasks":40}""")!.AsObject())!;
		Check(!going.Over, "still at it: not over");
		// ... and while it is still at it, what it has not done yet says nothing: 40 done, all 40 from the cache, is none
		// compiled so far - never 3,161 (a wait that can no longer ask it once took that for a cache out of date)
		Equal<long?>(0, going.CompiledSoFar, "a pass still going that has read every shader so far from its cache has compiled none");
		Equal<long?>(10, InGame.ReadShaderPass(JsonNode.Parse("""{"compiling":true,"totalTasks":3201,"completedTasks":50,"diskHitTasks":40}""")!.AsObject())!.CompiledSoFar,
			"one that has done more than it read from its cache has compiled the rest of what it did");
		Check(!InGame.ReadShaderPass(JsonNode.Parse("""{"compiling":false,"totalTasks":0,"completedTasks":0}""")!.AsObject())!.Over, "nothing queued yet (the game has only just come up): not over");
		Equal<long?>(null, InGame.ReadShaderPass(JsonNode.Parse("""{"compiling":false,"totalTasks":12,"completedTasks":12}""")!.AsObject())!.Compiled, "a shader mod that does not say what came from its cache");
		Equal<InGame.ShaderPass?>(null, InGame.ReadShaderPass(JsonNode.Parse("""{"cacheFiles":3201}""")!.AsObject()), "an answer that says nothing about a pass");
	}

	static void T_FollowersAreLivingTeammates()
	{
		var reply = JsonNode.Parse("""
			{ "refs": [
				{ "formId": "0x00000014", "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": "0x000A2C94", "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": 2228229, "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": "0x00033333", "actor": { "playerTeammate": true } },
				{ "formId": "0x000A2C94", "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": "0x0001A694", "actor": { "playerTeammate": false, "alive": true } },
				{ "formId": "0x0001B07F", "actor": { "playerTeammate": true, "alive": false } },
				{ "formId": "0x00012345" },
				{ "formId": "0x00054321", "actor": { "playerTeammate": "yes" } }
			] }
			""")!.AsObject();
		Equal("0x000A2C94,0x00220005", string.Join(",", InGame.Teammates(reply)), "living teammates only, never the player, once each");
		Equal(0, InGame.Teammates([]).Count, "no list = no followers");
	}

	// a teleport reads who is with the player first, then moves, waits for the new place and moves each living follower over
	static void T_FollowersFollowATeleport()
	{
		var (order, moved) = Teleport(false);
		Equal("read,move,wait,follow", order, "followers read before the move, moved after the new place");
		Equal("0x000A2C94 MoveTo 0x00000014", moved, "the living follower moved to the player, nobody else");
	}

	// a Stop that lands while the followers are read: no teleport, nobody moved
	static void T_StopDuringTheFollowerReadMovesNobody()
	{
		var (order, moved) = Teleport(true);
		Equal("read", order, "no move after a stop during the follower read");
		Equal("", moved, "no follower moved");
	}

	// the teleport against a local stand-in for DevBench (its actor list and its Papyrus calls) -> the call order and the moves
	static (string Order, string Moved) Teleport(bool a_stopAfterRead)
	{
		using var f = new FakeMo2();
		using var cts = new CancellationTokenSource();
		var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		probe.Start();
		var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		using var http = new System.Net.HttpListener();
		http.Prefixes.Add($"http://localhost:{port}/");
		http.Start();
		var order = new List<string>();
		var moved = new List<string>();
		var serve = Task.Run(() => {
			while (http.IsListening) {
				System.Net.HttpListenerContext c;
				try {
					c = http.GetContext();
				} catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException) {
					return;
				}
				using var reader = new StreamReader(c.Request.InputStream);
				var body = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? [];
				string reply;
				if (c.Request.Url!.AbsolutePath.EndsWith("/inspect", StringComparison.Ordinal)) {
					lock (order) {
						order.Add("read");
					}
					if (a_stopAfterRead) {
						cts.Cancel();
					}
					reply = """{"result":{"refs":[{"formId":"0x00000014","actor":{"playerTeammate":true,"alive":true}},{"formId":"0x000A2C94","actor":{"playerTeammate":true,"alive":true}},{"formId":"0x0001B07F","actor":{"playerTeammate":true,"alive":false}}]}}""";
				} else {
					lock (order) {
						order.Add("follow");
					}
					lock (moved) {
						moved.Add($"{body["self"]?["form"]} {body["function"]} {(body["args"] as JsonArray)?[0]?["form"]}");
					}
					reply = """{"result":{"called":true,"returned":null}}""";
				}
				var bytes = Encoding.UTF8.GetBytes(reply);
				c.Response.ContentType = "application/json";
				c.Response.OutputStream.Write(bytes);
				c.Response.Close();
			}
		});
		f.WriteSetup(("cleansave", new JsonObject()));
		var ctx = StepContext(f, "cleansave", new DevBench($"http://localhost:{port}/api/tool/"), cts.Token);
		Action teleport = () => InGame.MoveWithFollowers(ctx, () => { lock (order) { order.Add("move"); } }, () => { lock (order) { order.Add("wait"); } });
		if (a_stopAfterRead) {
			Throws<OperationCanceledException>(teleport, "a stop during the follower read ends the teleport");
		} else {
			teleport();
		}
		ctx.Log.Dispose();
		http.Stop();
		serve.Wait(TimeSpan.FromSeconds(5));
		lock (order) {
			return (string.Join(",", order), string.Join(";", moved));
		}
	}

	// A pop-up in the game is found and closed whatever it is. Against a local stand-in for DevBench that shows one box
	// after another: the ones the run knows get their answer, one it does not know is closed by the button that takes
	// nothing on and goes into the run's notes, a setup may leave such a box to the player, and one that will not close is
	// not answered for ever.
	static void T_AGameBoxIsClosedWhateverItIs()
	{
		using var f = new FakeMo2();
		var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		probe.Start();
		var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		using var http = new System.Net.HttpListener();
		http.Prefixes.Add($"http://localhost:{port}/");
		http.Start();
		// the boxes the made-up game shows, first to last; closes: false = its answer never takes
		var boxes = new Queue<(string Body, string[] Buttons, int Cancel)>();
		var accepted = new List<int>();
		var closes = true;
		// the game's own tutorial pop-up (no message box): shown or not, and the menus the run closed by name
		var tutorial = false;
		var closedMenus = new List<string>();
		// the game's answer to "describe" held back until a test lets it go (a look that is still reading)
		ManualResetEventSlim? holdDescribe = null;
		using var describeAsked = new ManualResetEventSlim();
		var serve = Task.Run(() => {
			while (http.IsListening) {
				System.Net.HttpListenerContext c;
				try {
					c = http.GetContext();
				} catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException) {
					return;
				}
				using var reader = new StreamReader(c.Request.InputStream);
				var asked = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? [];
				if (asked["action"]?.ToString() == "describe" && Volatile.Read(ref holdDescribe) is { } held) {
					describeAsked.Set();
					held.Wait(TimeSpan.FromSeconds(10));
				}
				var reply = new JsonObject();
				lock (boxes) {
					switch (asked["action"]?.ToString()) {
					case "list":
						reply["messageBoxOpen"] = boxes.Count > 0;
						reply["openMenus"] = tutorial ? new JsonArray("Main Menu", "Tutorial Menu") : new JsonArray("Main Menu");
						break;
					case "close":
						closedMenus.Add(asked["name"]?.ToString() ?? "");
						tutorial = false;
						break;
					case "describe" when boxes.Count > 0:
						var (body, buttons, cancel) = boxes.Peek();
						reply["messageBoxOpen"] = true;
						reply["bodyText"] = body;
						reply["buttons"] = new JsonArray(buttons.Select(b => (JsonNode?)b).ToArray());
						reply["cancelIndex"] = cancel;
						break;
					case "describe":
						reply["messageBoxOpen"] = false;
						break;
					case "call":
						// (the game's own words for its boxes, asked for a box that is not in English: this game has none to give)
						reply["called"] = true;
						reply["returned"] = "";
						break;
					case "accept":
						accepted.Add(asked["index"]?.GetValue<int>() ?? -1);
						if (closes && boxes.Count > 0) {
							boxes.Dequeue();
						}
						break;
					}
				}
				var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["result"] = reply }.ToJsonString());
				c.Response.ContentType = "application/json";
				c.Response.OutputStream.Write(bytes);
				c.Response.Close();
			}
		});
		try {
			var setup = f.WriteSetup(("cleansave", new JsonObject()));
			var ctx = StepContext(f, "cleansave", new DevBench($"http://localhost:{port}/api/tool/"), CancellationToken.None);
			using (ctx.Log) {
				Equal(null, InGame.LookAtGameBox(ctx, out var none, true), "no box open: nothing to do");
				Check(none == null && accepted.Count == 0, "and nothing pressed");
				// the game's tutorial pop-up is no message box, and stops the game under it all the same: closed by its name
				tutorial = true;
				Equal(null, InGame.LookAtGameBox(ctx, out _, true), "a tutorial pop-up is no message box");
				Equal("Tutorial Menu", string.Join(",", closedMenus), "and it is closed at the look that finds it");
				InGame.LookAtGameBox(ctx, out _, true);
				Equal(1, closedMenus.Count, "once: nothing is closed that is not there");
				lock (boxes) {
					boxes.Enqueue(("Grass cache files are missing. You will see no grass unless you generate a cache.", ["Ok"], -1));
					boxes.Enqueue(("This save relies on missing content. Would you like to search for your unidentified content in the Creations menu?", ["Yes", "No"], -1));
					boxes.Enqueue(("The following content is not present. Continue loading?", ["Yes", "No"], -1));
					boxes.Enqueue(("Some Mod: a new version changed its settings. Reset them now?", ["Reset", "Keep mine", "Ask me later"], 2));
					boxes.Enqueue(("Another Mod: delete its old data?", ["Yes", "No"], -1));
					boxes.Enqueue(("Choose your path", ["Warrior", "Mage", "Thief"], -1));
				}
				for (var look = 0; look < 6; ++look) {
					Check(InGame.LookAtGameBox(ctx, out var left, true) != null && left == null, $"box {look + 1} is answered or closed at the look that finds it");
				}
				Equal("0,1,0,2,1,2", string.Join(",", accepted), "a notice acknowledged; the missing-content questions No then Yes; a box the run does not know by the game's own cancel button, " +
					"by its declining button, and - with no button that declines - by its last");
				Equal(null, InGame.LookAtGameBox(ctx, out _, true), "and the game shows nothing more");
				// what the watch answered is there for the step in hand, and what it closed without knowing it is in the run's notes
				var told = 0;
				while (ctx.TakeAnsweredBox() != null) {
					++told;
				}
				Equal(6, told, "each box the watch answered is handed to the step in hand, once");
				ctx.AddWatchNotes();
				Equal(3, ctx.Journal.Notes.Count(n => n.Contains("this run does not know", StringComparison.Ordinal)), "the three it did not know are in the run's notes: " + string.Join(" | ", ctx.Journal.Notes));
				Check(ctx.Journal.Notes.Any(n => n.Contains("\"Ask me later\"", StringComparison.Ordinal) && n.Contains("Some Mod", StringComparison.Ordinal)), "each with what it was closed with");
				Check(!ctx.Journal.Notes.Any(n => n.Contains("Grass cache", StringComparison.Ordinal)), "a box the run knows is no note");
				// a box that will not close is not answered for ever: after its tries it is left, for the watch to name and stop on
				accepted.Clear();
				closes = false;
				lock (boxes) {
					boxes.Enqueue(("A notice that stays", ["Ok"], -1));
				}
				InGame.GameBox? stuck = null;
				for (var look = 0; look < 8 && stuck == null; ++look) {
					InGame.LookAtGameBox(ctx, out stuck, true);
				}
				Check(stuck is { Body: "A notice that stays" } && accepted.Count == 5, $"answered five times, then left as a box that does not close ({accepted.Count} answers)");
				lock (boxes) {
					boxes.Clear();
				}
				closes = true;
				Equal(null, InGame.LookAtGameBox(ctx, out _, true), "(gone)");
			}
			// a setup that leaves a box the run does not know to the player: never pressed, and handed on unanswered
			setup.Root[InGame.UnknownBoxSetting] = "stop";
			setup.Save();
			Check(!InGame.ClosesUnknown(Setup.Load(f.SetupFile)) && InGame.ClosesUnknown(Setup.FromJson(new JsonObject())), "close is the rule for a setup that says nothing; \"stop\" leaves such boxes to the player");
			ctx = StepContext(f, "cleansave", new DevBench($"http://localhost:{port}/api/tool/"), CancellationToken.None);
			using (ctx.Log) {
				accepted.Clear();
				lock (boxes) {
					boxes.Enqueue(("Another Mod: delete its old data?", ["Yes", "No"], -1));
				}
				Equal(null, InGame.LookAtGameBox(ctx, out var theirs, true), "left to the player: not answered");
				Check(theirs is { Body: "Another Mod: delete its old data?" } && accepted.Count == 0, "handed on as it is, nothing pressed");
				lock (boxes) {
					boxes.Clear();
					boxes.Enqueue(("Grass cache files are missing.", ["Ok"], -1));
				}
				Check(InGame.LookAtGameBox(ctx, out var known, true) != null && known == null && accepted.Count == 1, "a box the run knows is answered all the same");
			}
			// The watch's own look answers only while the run holds the watch: a look that was
			// still reading when the run let go sends no answer into a game the run has left. A step's own look answers as ever
			ctx = StepContext(f, "cleansave", new DevBench($"http://localhost:{port}/api/tool/"), CancellationToken.None);
			using (ctx.Log) {
				using var watch = new BoxWatch(() => [], ctx.Log, _ => { });
				var watched = new RunContext { Setup = ctx.Setup, Mo2 = ctx.Mo2, Profile = ctx.Profile, Control = ctx.Control, Log = ctx.Log, Journal = ctx.Journal,
					Cancel = CancellationToken.None, BackupFolder = ctx.BackupFolder, DevBench = ctx.DevBench, Boxes = watch };
				accepted.Clear();
				lock (boxes) {
					boxes.Clear();
					boxes.Enqueue(("Grass cache files are missing.", ["Ok"], -1));
				}
				Check(InGame.LookAtGameBox(watched, out _, true) != null && accepted.Count == 1, "while the run holds the watch, its look answers the game's box");
				// what the watch answered during one game start is not the next start's: dropped where a game start begins
				watched.GameLaunched = DateTime.Now;
				Equal(null, watched.TakeAnsweredBox(), "a box answered during an earlier start is not handed to the next start's load");
				lock (boxes) {
					boxes.Enqueue(("Perks were reset.", ["Ok"], -1));
				}
				Check(InGame.LookAtGameBox(watched, out _, true) != null && watched.TakeAnsweredBox() == "Perks were reset.", "one answered during this start is");
				// The run lets go of the watch WHILE its look is reading the box (the game's answer to "describe" is held back
				// until then): a guard that only looked as the look began would still send the answer afterwards
				lock (boxes) {
					boxes.Enqueue(("A late notice.", ["Ok"], -1));
				}
				var before = accepted.Count;
				using var letDescribeGo = new ManualResetEventSlim();
				Volatile.Write(ref holdDescribe, letDescribeGo);
				var reading = Task.Run(() => InGame.LookAtGameBox(watched, out _, true));
				Check(describeAsked.Wait(TimeSpan.FromSeconds(10)), "(the watch's look is reading the game's box)");
				watch.Dispose();
				Volatile.Write(ref holdDescribe, null);
				letDescribeGo.Set();
				Check(reading.Wait(TimeSpan.FromSeconds(10)) && reading.Result == null && accepted.Count == before, "let go of while its look was reading, the watch sends no answer to the game");
				Check(InGame.LookAtGameBox(watched, out _, true) == null && accepted.Count == before, "nor at a look that begins after it");
				Check(InGame.LookAtGameBox(watched, out _, false) != null && accepted.Count == before + 1, "a step's own look answers as ever");
			}
		} finally {
			http.Stop();
			serve.Wait(TimeSpan.FromSeconds(5));
		}
	}

	static void T_CompilingSignals()
	{
		using var f = new FakeMo2();
		var logs = Path.Combine(f.Root, "Logs");
		Directory.CreateDirectory(logs);
		var log = Path.Combine(logs, "BottledShaders.log");
		string Dated(double a_minutesAgo) => DateTime.Now.AddMinutes(-a_minutesAgo).ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
		File.WriteAllText(log, $"[{Dated(0.5)}] [info] [ShaderTiming] x remaining=2707\n");
		Check(InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "dated stamp, remaining > 0");
		// a count above 0 that is old by its own date, under newer lines of other kinds (Bottled Shaders ends a compile that way)
		File.WriteAllText(log, $"[{Dated(7)}] [info] [ShaderTiming] Very slow x remaining=549\n[{Dated(0.1)}] [info] [ReverseZ.cpp:683] ReverseZ: camera\n");
		Check(!InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-10)), "a slow-shader count 7 minutes old is not a compile, whatever the log gained since");
		File.WriteAllText(log, "[17:50:34.017] [info] [ShaderTiming] x remaining=0\n");
		Check(!InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "remaining 0 = done");
		var open = Path.Combine(logs, "OpenShaders.log");
		File.WriteAllText(open, "[info] Shader compilation started\n");
		// the newer log by its write time (a fast machine writes both in the same clock tick)
		File.SetLastWriteTimeUtc(open, File.GetLastWriteTimeUtc(log).AddSeconds(5));
		Check(InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "Open Shaders: started, no completed (the newest log wins)");
		File.AppendAllText(open, "[info] Shader compilation completed: 3169/3169 tasks\n");
		File.SetLastWriteTimeUtc(open, File.GetLastWriteTimeUtc(log).AddSeconds(10));
		Check(!InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "completed");
		var cache = Path.Combine(f.Overwrite, "ShaderCache");
		Directory.CreateDirectory(cache);
		File.WriteAllText(Path.Combine(cache, "a.bin"), "x");
		Check(InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "a fresh cache file = compiling, whatever the log says");
		Directory.Delete(cache, true);
		Check(!InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(10)), "a log older than the start does not count");
	}

	static void T_ShaderFingerprintChangesWithTheMod()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		f.Mod("Shader Mod");
		var dll = Path.Combine(f.Mods, "Shader Mod", "SKSE", "Plugins", "Shaders.dll");
		Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
		File.WriteAllText(dll, "v1");
		var c = new ShaderStep.Cache("Shader Mod", "Cache Mod", null);
		var a = ShaderStep.Fingerprint(mo2, c);
		File.WriteAllText(dll, "v2");
		Check(a != ShaderStep.Fingerprint(mo2, c), "a new DLL = a new fingerprint");
		Check(!ShaderStep.UpToDate(mo2, c), "no cache = not up to date");
		// the shader mod's meta.ini counts by the version it names: what Mod Organizer writes there on its own (an update
		// check, an endorsement) never makes the cache old - only the old way of reading a stamp counts the whole file
		var meta = Path.Combine(f.Mods, "Shader Mod", "meta.ini");
		File.WriteAllText(meta, "[General]\r\nmodid=192816\r\nversion=2.0.1\r\ninstallationFile=Shader Mod 2.0.1.7z\r\nlastNexusQuery=2026-11-01T10:00:00Z\r\nendorsed=0\r\n");
		var queried = ShaderStep.Fingerprint(mo2, c);
		var queriedOld = ShaderStep.Fingerprint(mo2, c, null, null, null, true, true, false);
		File.WriteAllText(meta, "[General]\r\nmodid=192816\r\nversion=2.0.1\r\ninstallationFile=Shader Mod 2.0.1.7z\r\nlastNexusQuery=2026-11-24T15:00:00Z\r\nendorsed=1\r\n");
		Equal(queried, ShaderStep.Fingerprint(mo2, c), "an update check or an endorsement leaves it as it was");
		Check(queriedOld != ShaderStep.Fingerprint(mo2, c, null, null, null, true, true, false), "(the old way counted the whole file)");
		File.WriteAllText(meta, "[General]\r\nmodid=192816\r\nversion=2.0.2\r\ninstallationFile=Shader Mod 2.0.2.7z\r\nlastNexusQuery=2026-11-24T15:00:00Z\r\nendorsed=1\r\n");
		Check(queried != ShaderStep.Fingerprint(mo2, c), "a new version of the shader mod makes it new");
		// with the profile: a feature mod's shader file changes it; what a generator writes (a plugin, a mesh) never does
		var p = ProfileOf(f);
		var feature = Path.Combine(f.Mods, "Base Mod", "Shaders", "Features", "Grass.ini");
		Directory.CreateDirectory(Path.GetDirectoryName(feature)!);
		File.WriteAllText(feature, "[Info]\r\nVersion=1-0-0\r\n");
		var withProfile = ShaderStep.Fingerprint(mo2, c, p);
		File.WriteAllText(feature, "[Info]\r\nVersion=1-1-0\r\n");
		var changed = ShaderStep.Fingerprint(mo2, c, p);
		Check(withProfile != changed, "a ticked mod's shader file is part of it");
		FakeMo2.Plugin(Path.Combine(f.Mods, "Output A", "OutA.esp"), true);
		var mesh = Path.Combine(f.Mods, "Output A", "Meshes", "lod.nif");
		Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
		File.WriteAllText(mesh, "a rebuilt LOD mesh");
		Equal(changed, ShaderStep.Fingerprint(mo2, c, p), "a rebuilt plugin or mesh leaves it as it was");
		// two ticked mods that ship the same shader file: the game reads the winner's, so which one wins is part of it
		foreach (var m in new[] { "Look A", "Look B" }) {
			f.Mod(m);
			Directory.CreateDirectory(Path.Combine(f.Mods, m, "Shaders"));
			File.WriteAllText(Path.Combine(f.Mods, m, "Shaders", "Lighting.hlsl"), m);
		}
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		var rest = File.ReadAllText(list);
		File.WriteAllText(list, "+Look A\r\n+Look B\r\n" + rest);
		var aWins = ShaderStep.Fingerprint(mo2, c, p);
		File.WriteAllText(list, "+Look B\r\n+Look A\r\n" + rest);
		Check(aWins != ShaderStep.Fingerprint(mo2, c, p), "the other mod wins the shared file now: a new fingerprint");
		// a stamp from before this was known is read the way it was written: no such cache is built again for it
		var older = ShaderStep.Fingerprint(mo2, c, p, null, null, false);
		File.WriteAllText(list, "+Look A\r\n+Look B\r\n" + rest);
		Check(older == ShaderStep.Fingerprint(mo2, c, p, null, null, false) && older != aWins, "the older fingerprint knows no winner, and is still what an older stamp holds");
		File.WriteAllText(list, "+Look B\r\n+Look A\r\n" + rest);
		// ... and only then: with no file shared, the order of the two changes nothing
		File.Move(Path.Combine(f.Mods, "Look B", "Shaders", "Lighting.hlsl"), Path.Combine(f.Mods, "Look B", "Shaders", "Water.hlsl"));
		var apart = ShaderStep.Fingerprint(mo2, c, p);
		File.WriteAllText(list, "+Look A\r\n+Look B\r\n" + rest);
		Equal(apart, ShaderStep.Fingerprint(mo2, c, p), "an order that changes no winner leaves the fingerprint, and the cache, as they were");

		// The shader mod's settings file counts by the features it switches off at the game's start and by nothing else in it:
		// the game rewrites the rest by itself (three preset fields changed at a start, and the next run built the whole cache
		// again for it)
		var settings = Path.Combine(f.Mods, "Shader Mod", "SKSE", "Plugins", "CommunityShaders", "SettingsUser.json");
		Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
		f.Mod("Cache Mod");
		var withCacheMod = ShaderStep.Fingerprint(mo2, c, p);
		File.WriteAllText(settings, """{ "Menu": { "LastSettingsSection": "General" }, "Presets": { "activePackId": "legacy:effects11" }, "Disable at Boot": { "Grass Lighting": false, "Skylighting": true } }""");
		var set = ShaderStep.Fingerprint(mo2, c, p);
		var setOld = ShaderStep.Fingerprint(mo2, c, p, null, null, true, false);
		Check(set != withCacheMod, "(the settings file is part of it)");
		File.WriteAllText(settings, """{ "Presets": { "activePackId": "" }, "Menu": { "LastSettingsSection": "ShadersCache" }, "Disable at Boot": { "Skylighting": true, "Grass Lighting": false } }""");
		Equal(set, ShaderStep.Fingerprint(mo2, c, p), "values the game rewrites by itself (the menu's last page, the active preset) leave the cache current");
		Check(setOld != ShaderStep.Fingerprint(mo2, c, p, null, null, true, false), "read the way an older stamp was written, every value still counts");
		// a stamp says which way it was written, and is read that way
		var cacheDir = Path.Combine(f.Mods, "Cache Mod", "ShaderCache");
		Directory.CreateDirectory(cacheDir);
		File.WriteAllText(Path.Combine(cacheDir, "a shader.bin"), "compiled");
		var stamp = Path.Combine(f.Mods, "Cache Mod", Brand.Name + " cache.json");
		File.WriteAllText(stamp, new JsonObject { ["fingerprint"] = set, ["files"] = 1, ["verified"] = true, ["winners"] = true, ["settings"] = "boot", ["meta"] = "version" }.ToJsonString());
		Equal(null, ShaderStep.WhyStale(mo2, c, p), "a cache stamped the new way is current");
		File.WriteAllText(settings, """{ "Presets": { "activePackId": "another" }, "Disable at Boot": { "Skylighting": true, "Grass Lighting": false } }""");
		Check(ShaderStep.UpToDate(mo2, c, p), "and stays current when the game rewrites its settings");
		File.WriteAllText(settings, """{ "Presets": { "activePackId": "another" }, "Disable at Boot": { "Skylighting": false, "Grass Lighting": false } }""");
		Check((ShaderStep.WhyStale(mo2, c, p) ?? "").Contains("features switched off at the game's start", StringComparison.Ordinal),
			"a feature switched at the game's start is another cache, and the reason is said: " + ShaderStep.WhyStale(mo2, c, p));
		File.WriteAllText(stamp, new JsonObject { ["fingerprint"] = ShaderStep.Fingerprint(mo2, c, p, null, null, true, false, false), ["files"] = 1, ["verified"] = true, ["winners"] = true }.ToJsonString());
		Equal(null, ShaderStep.WhyStale(mo2, c, p), "an older stamp (every value counted) is read the way it was written: no cache is built again for the change");
		// one written while settings counted by their boot part but meta.ini whole: read that way too
		File.WriteAllText(stamp, new JsonObject { ["fingerprint"] = ShaderStep.Fingerprint(mo2, c, p, null, null, true, true, false), ["files"] = 1, ["verified"] = true, ["winners"] = true, ["settings"] = "boot" }.ToJsonString());
		Equal(null, ShaderStep.WhyStale(mo2, c, p), "a stamp from before meta.ini counted by its version is read the way it was written");
		File.Delete(Path.Combine(cacheDir, "a shader.bin"));
		Check((ShaderStep.WhyStale(mo2, c, p) ?? "").Contains("holds 0 shader(s)", StringComparison.Ordinal), "a cache that lost its shaders says so: " + ShaderStep.WhyStale(mo2, c, p));
		File.Delete(stamp);
		Check((ShaderStep.WhyStale(mo2, c, p) ?? "").Contains("no record", StringComparison.Ordinal), "no stamp: built, and the reason is said");
		// a file saved with a byte order mark is the same settings
		File.WriteAllText(settings, """{ "Disable at Boot": { "Skylighting": true } }""", new UTF8Encoding(false));
		var plain = ShaderStep.Fingerprint(mo2, c, p);
		File.WriteAllText(settings, """{ "Menu": 1, "Disable at Boot": { "Skylighting": true } }""", new UTF8Encoding(true));
		Equal(plain, ShaderStep.Fingerprint(mo2, c, p), "a byte order mark in front of the settings changes nothing");
		// a settings file that does not read counts whole
		File.WriteAllText(settings, "not settings at all");
		var unread = ShaderStep.Fingerprint(mo2, c, p);
		File.WriteAllText(settings, "still not settings");
		Check(unread != ShaderStep.Fingerprint(mo2, c, p), "a settings file that does not read counts with all of it");
	}

	// a profile may keep a cache for each of two shader mods - switching the tick between them, or
	// updating one, must leave the other's cache current
	static void T_TwoShaderCachesStayApart()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		foreach (var m in new[] { "Shader A", "Shader B" }) {
			var hlsl = Path.Combine(f.Mods, m, "Shaders", "Common.hlsl");
			Directory.CreateDirectory(Path.GetDirectoryName(hlsl)!);
			File.WriteAllText(hlsl, m + " v1");
		}
		var ml = Path.Combine(f.ProfileDir, "modlist.txt");
		var baseList = f.Text("modlist.txt");
		var a = new ShaderStep.Cache("Shader A", "Cache A", null);
		var b = new ShaderStep.Cache("Shader B", "Cache B", null);
		var notA = ShaderStep.OtherCacheMods([a, b], a);
		var p = ProfileOf(f);
		File.WriteAllText(ml, "+Shader A\r\n-Shader B\r\n" + baseList);
		var aTicked = ShaderStep.Fingerprint(mo2, a, p, notA);
		File.WriteAllText(ml, "-Shader A\r\n+Shader B\r\n" + baseList);
		Equal(aTicked, ShaderStep.Fingerprint(mo2, a, p, notA), "the tick moved to the other shader mod: A's cache is still current");
		File.WriteAllText(Path.Combine(f.Mods, "Shader B", "Shaders", "Common.hlsl"), "Shader B v2");
		Equal(aTicked, ShaderStep.Fingerprint(mo2, a, p, notA), "the other shader mod updated: A's cache is still current");
		File.WriteAllText(Path.Combine(f.Mods, "Shader A", "Shaders", "Common.hlsl"), "Shader A v2");
		Check(aTicked != ShaderStep.Fingerprint(mo2, a, p, notA), "its own shader files changed (ticked or not): A's cache is stale");
	}

	// the final save never starts from an old clean save when the player played after it
	static void T_SaveToPlayStartsFromTheRightSave()
	{
		var mon = new DateTime(2026, 10, 26, 3, 0, 0, DateTimeKind.Utc);
		var thu = mon.AddDays(3);
		Equal("Tool Runner - clean save", InGame.PlaySaveFrom("Tool Runner - clean save", null, mon, "Save9", thu).Save, "this run's clean save first");
		Equal("Named", InGame.PlaySaveFrom(null, "Named", mon, "Save9", thu).Save, "then the one the setup names");
		Equal("Save9", InGame.PlaySaveFrom(null, null, mon, "Save9", thu).Save, "an old clean save loses to newer progress of the player's");
		Equal("Tool Runner - clean save", InGame.PlaySaveFrom(null, null, thu, "Save9", mon).Save, "a clean save newer than every save of theirs is used");
		Equal("Save9", InGame.PlaySaveFrom(null, null, null, "Save9", mon).Save, "no clean save: the player's newest");
		Equal<string?>(null, InGame.PlaySaveFrom(null, null, null, null, null).Save, "nothing at all: no save");
		// only the runner's GAME steps count as its own time - a save the player made while a tool step ran is theirs
		var steps = new[] {
			new StepRecord { Key = "cleansave", Started = mon, Ended = mon.AddMinutes(5) },
			new StepRecord { Key = "dyndolod", Started = mon.AddMinutes(5), Ended = mon.AddMinutes(30) },
			new StepRecord { Key = "playsave", Started = mon.AddMinutes(30), Ended = null },
			new StepRecord { Key = "pandora", Started = null },
		};
		var times = InGame.GameTimes(steps, mon.AddMinutes(40)).ToList();
		Equal(2, times.Count, "the clean save and the final save");
		Check(!times.Any(t => mon.AddMinutes(15) >= t.From && mon.AddMinutes(15) <= t.To), "a save made during DynDOLOD is the player's");
		// a run that died in a game step - its open step ends where its record stopped, so a save made a
		// day later is still the player's
		var died = InGame.GameTimes([new StepRecord { Key = "cleansave", Started = mon, Ended = null }], mon.AddMinutes(3)).Single();
		Check(!(thu >= died.From && thu <= died.To), "a save three days after a crashed run is the player's");
		Equal(mon.AddMinutes(3), died.To, "the open step ends where the dead run's record stopped");
	}

	// a patcher's CPU counts per child process - one that ends never makes it fall, a new one counts whole
	static void T_ChildCpuGain()
	{
		var before = new Dictionary<int, TimeSpan> { [1] = TimeSpan.FromSeconds(5), [2] = TimeSpan.FromSeconds(9) };
		var now = new Dictionary<int, TimeSpan> { [1] = TimeSpan.FromSeconds(5.1), [3] = TimeSpan.FromSeconds(2) };
		Equal(TimeSpan.FromSeconds(2.1), Procs.CpuGain(before, now), "child 2 ended (not a loss), child 3 is new (whole), child 1 +0.1 s");
		Equal(TimeSpan.Zero, Procs.CpuGain(now, now), "nothing used: no gain");
	}

	// a form id read either way, a setup read from bytes, a task's own setup file
	static void T_FormIdsAndSetupsAreReadHoweverWritten()
	{
		Equal("0x0000003B", DevBench.FormKey(JsonValue.Create("0x3B")), "0x is hex");
		Equal("0xFF000801", DevBench.FormKey(JsonValue.Create("FF000801")), "an 8-digit id is hex");
		Equal("0x0000007B", DevBench.FormKey(JsonValue.Create("123")), "a plain number is decimal");
		Equal("0x0000003B", DevBench.FormKey(JsonValue.Create("0000003B")), "a padded 8-digit id is hex");
		Equal("0x00BC614E", DevBench.FormKey(JsonValue.Create("12345678")), "an 8-digit decimal is decimal");
		Equal("0x0000003B", DevBench.FormKey(JsonValue.Create(59)), "a JSON number");
		var s = Setup.Parse(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes("{\"schema\":1,\"name\":\"From bytes\"}")).ToArray(), @"C:\x.json");
		Equal("From bytes", s.Name, "a setup read from its bytes (a BOM in front)");
	}

	static void T_CheckBoxPixels()
	{
		var px = new int[20 * 3];
		for (int x = 3; x < 12; ++x) {
			px[20 + x] = unchecked((int)0xFF2060E0);
		}
		Check(CheckList.IsTicked(new Picture(20, 3, px), 3, 15, 1), "a filled box is ticked");
		Check(!CheckList.IsTicked(new Picture(20, 3, new int[60]), 3, 15, 1), "a grey box is not");
	}

	// ------------------------------------------------------------------ the whole run, with test steps
	sealed class FakeStep(string a_key, Action<RunContext, StepSettings> a_run) : IStep
	{
		public string Key => a_key;
		public void Run(RunContext a_ctx, StepSettings a_s) => a_run(a_ctx, a_s);
		public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s) => [];
	}

	static void WriteOutput(RunContext a_ctx, string a_step, string a_mod, string a_plugin)
	{
		StepKit.ParkMod(a_ctx, a_step, a_mod);
		FakeMo2.Plugin(Path.Combine(a_ctx.Mo2.ModDir(a_mod), a_plugin), false);
		File.WriteAllText(Path.Combine(a_ctx.Mo2.ModDir(a_mod), "new.txt"), "fresh");
		a_ctx.SetAfter(a_mod);
	}

	// A whole run with the directory cache mod loading its cache blind: the final save makes the game's list anew after
	// everything the run changed, so nothing is left to say - and a command of the setup that runs after it counts as a
	// change again, said with what to do if it changes game files
	static void T_ACommandAfterTheRunCountsAsAChange()
	{
		using var f = new FakeMo2();
		f.Mod("CRDW");
		var modlist = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(modlist, File.ReadAllText(modlist).Replace("+Base Mod", "+CRDW\r\n+Base Mod", StringComparison.Ordinal));
		var own = Path.Combine(f.Mods, "CRDW", Crdw.IniRel);
		Directory.CreateDirectory(Path.GetDirectoryName(own)!);
		File.WriteAllText(Path.Combine(Path.GetDirectoryName(own)!, "CRDW.dll"), "x");
		var asThePlayerHasIt = "[General]\r\nShowPopUp=b:false\r\nMode=s:LOAD\r\n";
		File.WriteAllText(own, asThePlayerHasIt);
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		// the final save's game start, as far as the game's list goes
		var modeAtTheStart = "";
		StepRegistry.Replace(new FakeStep("playsave", (ctx, s) => {
			GameStart.Prepare(ctx);
			modeAtTheStart = Crdw.Parse(File.ReadAllText(own)).Mode;
		}));
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("playsave", []));
		setup.Root["skseLogs"] = Path.Combine(f.Root, "no logs here");
		setup.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		Equal("CACHE", modeAtTheStart, "the final save's game start makes the list anew: the run changed the game's files before it");
		var j = Journal.Load(r.Folder!);
		Equal(0, j.FilesChanged, "and nothing changed after that list was made");
		Check(!j.Notes.Any(n => n.Contains("Cached Recursive Directory Walk", StringComparison.Ordinal)), "so nothing is left to say about it: " + string.Join(" | ", j.Notes));
		Equal(asThePlayerHasIt, File.ReadAllText(own), "the mod's own file is back as the player had it");
		Check(!j.NeedsRestore, "and nothing is left to put back");
		var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
		setup = Setup.Load(f.SetupFile);
		setup.Root["afterRun"] = new JsonArray($"\"{cmd}\" /c exit 0");
		setup.Save();
		r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		j = Journal.Load(r.Folder!);
		Equal(1, j.FilesChanged, "a command of the setup ran after that list was made: counted as a change, whatever the command did");
		Check(j.Notes.Any(n => n.Contains("Cached Recursive Directory Walk", StringComparison.Ordinal) && n.Contains("command after the run", StringComparison.Ordinal)),
			"and said, with what to do if the command changes game files: " + string.Join(" | ", j.Notes));
	}

	static void T_RunSwitchesOutputsOffAndBuiltOnesBackOn()
	{
		using var f = new FakeMo2();
		var seenOff = false;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			seenOff = !ctx.Profile.IsOn("Output A") && !ctx.Profile.IsOn("Output B");
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => WriteOutput(ctx, "synthesis", "Output B", "OutB.esp")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Output B" }));
		var before = f.Text("loadorder.txt");
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		Check(seenOff, "every output was OFF while the first tool ran");
		var p = ProfileOf(f);
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "built outputs on, everything else as before");
		Equal(before, f.Text("loadorder.txt"), "the load order byte for byte");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")), "the new output is in its mod");
		var zips = Directory.GetFiles(f.Backups, RunUndo.OutputsZip, SearchOption.AllDirectories);
		Check(zips.Length == 1, "the old outputs are packed in the backup");
		using var packed = System.IO.Compression.ZipFile.OpenRead(zips[0]);
		Check(packed.Entries.Any(e => e.Name == "OutA.esp"), "the old output is in it");
	}

	// A step may use the output its tool made before. Its tool does not run; the old output goes off with
	// the others before the first tool, comes back on at the step's own place - so the tool before it never reads it and the tool
	// after it does -, is on after the run, untouched, and the run says so in its record, its log and its summary
	static void T_AStepThatUsesItsOldOutputIsNotRunAndItsOutputComesBackAtItsPlace()
	{
		using var f = new FakeMo2();
		bool? offForTheToolBefore = null, onForTheToolAfter = null, pluginOnForTheToolAfter = null;
		var ranAnyway = false;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			offForTheToolBefore = !ctx.Profile.IsOn("Output B");
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		StepRegistry.Replace(new FakeStep("pgpatcher", (ctx, s) => ranAnyway = true));
		StepRegistry.Replace(new FakeStep("texgen", (ctx, s) => {
			onForTheToolAfter = ctx.Profile.IsOn("Output B");
			pluginOnForTheToolAfter = ctx.Profile.ActivePlugins().Contains("OutB.esp", StringComparer.OrdinalIgnoreCase);
			WriteOutput(ctx, "texgen", "Output C", "OutC.esp");
		}));
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("pgpatcher", new JsonObject { ["output_mod"] = "Output B" }),
			("texgen", new JsonObject { ["output_mod"] = "Output C" }));
		((JsonArray)setup.Root["steps"]!)[1]![OldOutput.Key] = true;
		setup.Save();
		Equal("landscapefixer,texgen", string.Join(",", setup.EnabledSteps.Select(x => x.Key)), "the step that uses its old output is not among the steps that run");
		Equal("pgpatcher", string.Join(",", setup.KeptSteps.Select(x => x.Key)), "it is the one whose old output is used");
		var was = File.ReadAllBytes(Path.Combine(f.Mods, "Output B", "OutB.esp"));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		Check(!ranAnyway, "its tool was not run");
		Check(offForTheToolBefore == true, "the old output was OFF while the tool before its place ran");
		Check(onForTheToolAfter == true, "and back ON for the tool after its place");
		Check(pluginOnForTheToolAfter == true, "with its plugin ticked");
		var p = ProfileOf(f);
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Output C") && p.IsOn("Needs B"), "after the run: what was built is on, the old output is on, and what needs it is on as before");
		Check(was.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(f.Mods, "Output B", "OutB.esp"))) && !File.Exists(Path.Combine(f.Mods, "Output B", "new.txt")), "the old output is as it was, byte for byte");
		var j = Journal.Load(r.Folder!);
		Equal("pgpatcher:Output B", string.Join(",", j.UsedOld.Select(u => $"{u.Step}:{u.Mod}")), "the run's record names the step and the old output it used");
		Check(j.Steps.All(x => x.Key != "pgpatcher") && !j.Built.Contains("Output B") && j.Parked.All(x => x.Mod != "Output B"), "it is no step the run ran, nothing it built, and nothing it moved into its backup");
		Check(j.Notes.Any(n => n.Contains("PGPatcher was not run: its old output \"Output B\" is used", StringComparison.Ordinal)), "the run's notes say so: " + string.Join(" | ", j.Notes));
		Check(RunSummary.Of(j).Lines().Any(l => l.Contains("Not built again: PGPatcher (Output B)", StringComparison.Ordinal)), "and its summary: " + string.Join(" | ", RunSummary.Of(j).Lines()));
		var log = File.ReadAllText(Path.Combine(r.Folder!, "log.txt"));
		// (the check's note at the run's start says "PGPatcher: not run ..." too: the step's own place is its "===" line)
		const string place = "=== PGPatcher: not run - its old output \"Output B\" is used";
		Check(log.Contains(place, StringComparison.Ordinal) && log.Contains("In the game: ", StringComparison.Ordinal) &&
		      log.Contains("Built again in this run before its place: Landscape Fixer", StringComparison.Ordinal), "the log carries the whole warning at the step's place");
		// (each line must be there before its place is compared: a line that is missing reads -1, which is "before" everything)
		var toolBefore = log.IndexOf("=== Landscape Fixer done", StringComparison.Ordinal);
		var itsPlace = log.IndexOf(place, StringComparison.Ordinal);
		var toolAfter = log.IndexOf("=== TexGen ===", StringComparison.Ordinal);
		Check(toolBefore >= 0 && itsPlace > toolBefore && toolAfter > itsPlace, "between the tool before it and the tool after it");
		var saidAtTheStart = log.IndexOf("PGPatcher: not run - its old output", StringComparison.Ordinal);
		var firstTool = log.IndexOf("=== Landscape Fixer ===", StringComparison.Ordinal);
		Check(saidAtTheStart >= 0 && firstTool > saidAtTheStart, "and the run's notes at its start say it before any tool runs");
		Check(j.UsedOld.Single() is { Done: true, Listed: false }, "on record as carried out, for a mod the list named already");
		Check(!LastRuns.Of(Journal.All(Runner.RunsDir(f.Data)), f.SetupFile).ContainsKey("pgpatcher"), "a step that was not run has no last run: its output is not called current because of this run");
		// asked for by name it is refused in words; set for a step that is switched off it means nothing
		try {
			Runner.Plan(setup, "pgpatcher", null, null);
			Check(false, "a run of only that step is refused");
		} catch (ArgumentException e) {
			Check(e.Message.Contains("is set to use its old output in this setup", StringComparison.Ordinal) && RunnerErrors.Find(e.Message)?.Code == "TR-107", "refused naming why: " + e.Message);
		}
		((JsonArray)setup.Root["steps"]!)[1]!["on"] = false;
		Check(setup.KeptSteps.Count == 0 && setup.EnabledSteps.All(x => x.Key != "pgpatcher"), "a step that is switched off neither runs nor has its old output switched by the run");
	}

	// ... and the two things that cannot work are refused before anything is touched: no old output there to use, and one whose
	// plugin needs a master that is nowhere in the load order (the game would not start). One that can be used is a note. A step
	// with nothing of its own to keep ignores the setting; a setup where nothing would run says so
	static void T_AnOldOutputThatCannotBeUsedStopsTheStart()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => { }));
		Setup With(string a_output, params string[] a_old)
		{
			var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = a_output }), ("cleansave", []));
			var steps = (JsonArray)setup.Root["steps"]!;
			foreach (var o in steps.OfType<JsonObject>().Where(o => a_old.Contains(o["step"]!.ToString()))) {
				o[OldOutput.Key] = true;
			}
			steps[2]!["on"] = a_old.Contains("cleansave");
			setup.Save();
			return setup;
		}
		With("No Such Output", "synthesis");
		var gate = Runner.CheckStart(new RunRequest(f.SetupFile, f.Data));
		Check(!gate.Starts && gate.Why.Contains("there is none to use: \"No Such Output\" is missing or holds nothing", StringComparison.Ordinal), "no old output there: " + gate.Why);
		Equal("TR-320", RunnerErrors.Find(gate.Why)?.Code ?? "", "with what to do about it");
		// an output mod that holds only Mod Organizer's own file is as good as none
		f.Mod("Empty Output");
		With("Empty Output", "synthesis");
		Check(Runner.CheckStart(new RunRequest(f.SetupFile, f.Data)).Why.Contains("\"Empty Output\" is missing or holds nothing", StringComparison.Ordinal), "an empty one is none");
		// ... and so is one that holds only folders with nothing in them (a cache whose files were cleared away): it takes a file
		Directory.CreateDirectory(Path.Combine(f.Mods, "Empty Output", "grass", "Tamriel"));
		Check(Runner.CheckStart(new RunRequest(f.SetupFile, f.Data)).Why.Contains("\"Empty Output\" is missing or holds nothing", StringComparison.Ordinal), "folders with nothing in them are no output");
		File.WriteAllText(Path.Combine(f.Mods, "Empty Output", "grass", "Tamriel", "x0y0.cgid"), "cache");
		gate = Runner.CheckStart(new RunRequest(f.SetupFile, f.Data));
		Check(gate.Starts, "a file in a folder of it makes it an output: " + gate.Why);
		// a run of only the shader caches or the final save switches no output off and none back on: an old output that
		// is not there is not that run's business, and does not stop it (the run of a tool is stopped, as above)
		// (a setup with a shader step that is switched on, asked for alone - and, as the control, together with a tool)
		var realShaders = StepRegistry.For("shaders")!;
		StepRegistry.Replace(new FakeStep("shaders", (ctx, s) => { }));
		try {
			var shaderSetup = f.WriteSetup(("shaders", []), ("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "No Such Output" }));
			((JsonArray)shaderSetup.Root["steps"]!)[2]![OldOutput.Key] = true;
			shaderSetup.Save();
			var shaderMo2 = Mo2Instance.Load(f.Exe, f.Instance);
			Equal("shaders,landscapefixer", string.Join(",", shaderSetup.EnabledSteps.Select(x => x.Key)), "(the shader step is one of the steps that run)");
			var onlyShaders = Preflight.Run(shaderSetup, shaderMo2, ["shaders"]);
			Check(!onlyShaders.Any(i => i.Step == "synthesis"), "a run of the shader caches alone says nothing about a step it never touches: " + string.Join(" | ", onlyShaders.Select(i => $"{i.Step}: {i.Text}")));
			var withATool = Preflight.Run(shaderSetup, shaderMo2, ["shaders", "landscapefixer"]);
			Check(withATool.Any(i => i.Step == "synthesis" && i.Blocking && i.Text.Contains("there is none to use", StringComparison.Ordinal)), "the same setup with a tool in the run is stopped over it");
		} finally {
			StepRegistry.Replace(realShaders);
		}
		f.Mod("Old Patch", ("OldPatch.esp", false, ["BaseMod.esp", "Gone.esp"]));
		With("Old Patch", "synthesis");
		gate = Runner.CheckStart(new RunRequest(f.SetupFile, f.Data));
		Check(!gate.Starts && gate.Why.Contains("its old output cannot be used: OldPatch.esp needs Gone.esp, which is no longer in your load order", StringComparison.Ordinal), "a master that is gone: " + gate.Why);
		Equal("TR-321", RunnerErrors.Find(gate.Why)?.Code ?? "", "with what to do about it");
		// (the mod list does not name "Old Patch" yet: the run adds its line and switches it on)
		With("Output B", "synthesis");
		gate = Runner.CheckStart(new RunRequest(f.SetupFile, f.Data));
		Check(gate.Starts, "an old output that is there, with every master in the load order, starts: " + gate.Why);
		Check(gate.Check.Any(i => !i.Blocking && i.Step == "synthesis" && i.Text.Contains("not run - its old output \"Output B\" is used", StringComparison.Ordinal)), "and is a note of the run");
		// a master the run itself builds is there (Output B's plugin needs Output A's, which is built again in this run)
		Check(OldOutput.LookAt(Setup.Load(f.SetupFile), Mo2Instance.Load(f.Exe, f.Instance), ProfileOf(f).States(), Setup.Load(f.SetupFile).EnabledSteps).Single().Usable, "a master in an output the run builds is not called gone");
		var odd = With("Output B", "cleansave");
		Check(odd.KeptSteps.Count == 0 && odd.EnabledSteps.Any(x => x.Key == "cleansave"), "a step with no output of its own runs as ever, whatever the setting says");
		Check(OldOutput.WhyNot("cleansave", new StepSettings([])) is { } why && why.Contains("makes no output of its own", StringComparison.Ordinal), "and the window says why it has nothing to use");
		Check(OldOutput.WhyNot("shaders", new StepSettings([]))!.Contains("Rebuild shader caches", StringComparison.Ordinal), "the shader caches point at their own setting");
		var none = Setup.FromJson(new JsonObject { ["mo2"] = new JsonObject { ["exe"] = f.Exe, ["profile"] = "Test" }, ["backups"] = f.Backups,
			["steps"] = new JsonArray(new JsonObject { ["step"] = "synthesis", [OldOutput.Key] = true, ["settings"] = new JsonObject { ["output_mod"] = "Output B" } }) });
		Check(none.Problems().Any(x => x.Contains("uses its old output - nothing would run", StringComparison.Ordinal) && RunnerErrors.Find(x)?.Code == "TR-112"), "a setup where every step uses its old output has nothing to run: " + string.Join(" | ", none.Problems()));
	}

	// A master of an old output's plugin is judged by what the run switches, not by what is ticked now:
	// one in a mod the run replaces is gone for good; one that is off while a tool works after the old output's
	// place stops that tool; one that is only off for the run, with no tool after the place, harms nothing
	static void T_AnOldOutputsMastersAreJudgedByWhatTheRunSwitches()
	{
		using var f = new FakeMo2();
		// (its own stand-ins for the tools that run here, so the test stands alone: the real steps' checks ask for their programs)
		foreach (var tool in new[] { "landscapefixer", "pgpatcher", "texgen" }) {
			StepRegistry.Replace(new FakeStep(tool, (ctx, s) => { }));
		}
		Setup With(params (string Step, string Output, bool Old, string[] Replaces)[] a_steps)
		{
			var setup = f.WriteSetup([.. a_steps.Select(s => (s.Step, new JsonObject { ["output_mod"] = s.Output, ["replaces"] = new JsonArray([.. s.Replaces.Select(r => (JsonNode?)r)]) }))]);
			var steps = (JsonArray)setup.Root["steps"]!;
			for (var i = 0; i < a_steps.Length; ++i) {
				if (a_steps[i].Old) {
					steps[i]![OldOutput.Key] = true;
				}
			}
			setup.Save();
			return setup;
		}
		// its master is in a mod that a tool of this run replaces: that mod stays off after the run, so the game would not start
		f.Mod("Patch On Old", ("PatchOnOld.esp", false, ["Old.esp"]));
		With(("landscapefixer", "Output A", false, ["Replaced"]), ("synthesis", "Patch On Old", true, []));
		var gate = Runner.CheckStart(new RunRequest(f.SetupFile, f.Data));
		Check(!gate.Starts && gate.Why.Contains("its old output cannot be used: PatchOnOld.esp needs Old.esp, which is in \"Replaced\" - a mod this run replaces and leaves switched off", StringComparison.Ordinal),
			"a master in a mod the run replaces: " + gate.Why);
		Equal("TR-321", RunnerErrors.Find(gate.Why)?.Code ?? "", "with what to do about it");
		With(("landscapefixer", "Output A", false, []), ("synthesis", "Patch On Old", true, []));
		gate = Runner.CheckStart(new RunRequest(f.SetupFile, f.Data));
		Check(gate.Starts, "the same master in a mod the run leaves alone is there: " + gate.Why);
		// its master is built only later in this run: the tool that builds it would read the old output's plugin without it
		With(("pgpatcher", "Output B", true, []), ("landscapefixer", "Output A", false, []));
		gate = Runner.CheckStart(new RunRequest(f.SetupFile, f.Data));
		Check(!gate.Starts && gate.Why.Contains("its old output cannot be used in this run: OutB.esp needs OutA.esp: it is built again only later in this run, by Landscape Fixer", StringComparison.Ordinal),
			"a master that is built only after the old output's place: " + gate.Why);
		Equal("TR-321", RunnerErrors.Find(gate.Why)?.Code ?? "", "with what to do about it");
		// its master is in a mod that goes off for the run (a plugin of that mod needs an output the run builds again), and a
		// tool works after the old output's place
		f.Mod("Patch On Needs", ("PatchOnNeeds.esp", false, ["NeedsB.esp"]));
		var both = With(("pgpatcher", "Output B", false, []), ("synthesis", "Patch On Needs", true, []), ("texgen", "Output C", false, []));
		gate = Runner.CheckStart(new RunRequest(f.SetupFile, f.Data));
		Check(!gate.Starts && gate.Why.Contains("PatchOnNeeds.esp needs NeedsB.esp: it is in \"Needs B\", which is switched off while the run's tools work", StringComparison.Ordinal),
			"a master in a mod that is off while a tool works after it: " + gate.Why);
		var look = OldOutput.LookAt(both, Mo2Instance.Load(f.Exe, f.Instance), ProfileOf(f).States(), both.EnabledSteps).Single();
		Check(look is { Usable: false, MissingMaster: null, OffInRun: not null } && OldOutput.Warning(look, "Test").Any(l => l.StartsWith("It cannot be used in this run: PatchOnNeeds.esp needs NeedsB.esp", StringComparison.Ordinal)),
			"the window's box and START's preview say it as well");
		// ... with no tool after its place it is switched on only as the run ends, when everything is back on: nothing reads it half
		With(("pgpatcher", "Output B", false, []), ("synthesis", "Patch On Needs", true, []));
		gate = Runner.CheckStart(new RunRequest(f.SetupFile, f.Data));
		Check(gate.Starts, "the same old output with no tool after its place starts: " + gate.Why);
		// a master that lies in Mod Organizer's overwrite folder is in the game's Data for every tool and for the game
		FakeMo2.Plugin(Path.Combine(f.Overwrite, "InOverwrite.esp"), false);
		f.Mod("Patch On Overwrite", ("PatchOnOverwrite.esp", false, ["InOverwrite.esp"]));
		With(("landscapefixer", "Output A", false, []), ("synthesis", "Patch On Overwrite", true, []), ("texgen", "Output C", false, []));
		gate = Runner.CheckStart(new RunRequest(f.SetupFile, f.Data));
		Check(gate.Starts, "a master in the overwrite folder is there: " + gate.Why);
		File.Delete(Path.Combine(f.Overwrite, "InOverwrite.esp"));
		gate = Runner.CheckStart(new RunRequest(f.SetupFile, f.Data));
		Check(!gate.Starts && gate.Why.Contains("PatchOnOverwrite.esp needs InOverwrite.esp, which is no longer in your load order", StringComparison.Ordinal), "and one that is nowhere still stops the start: " + gate.Why);
		// the profile a run's end leaves is what counts for the game: a master whose mod was switched off after the check
		// (a command of the setup before the run) is not there, whatever was ticked when the check looked
		f.Mod("Old Patch", ("OldPatch.esp", false, ["BaseMod.esp"]));
		var last = With(("landscapefixer", "Output A", false, []), ("synthesis", "Old Patch", true, []));
		var lastMo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Equal<string?>(null, OldOutput.MasterGoneAfterRun(last, lastMo2, ProfileOf(f).States(), last.EnabledSteps, "Old Patch"), "with its master's mod on as the run began, the old output is good for the run's end");
		var switchedOff = ProfileOf(f).States();
		switchedOff["Base Mod"] = '-';
		Check(OldOutput.MasterGoneAfterRun(last, lastMo2, switchedOff, last.EnabledSteps, "Old Patch") is { } goneAfter && goneAfter.StartsWith("OldPatch.esp needs BaseMod.esp", StringComparison.Ordinal),
			"with that mod switched off by then, its master is not there once the run has ended");

		// Two old outputs that come on before the same tool, the one listed first needing the other's plugin: no tool works
		// between them, so the run takes them as one - the one that is needed is switched on first - and the tool finds both
		using var g = new FakeMo2();
		g.Mod("Base Out", ("BaseOut.esp", false, []));
		g.Mod("Patch On Base", ("PatchOnBase.esp", false, ["BaseOut.esp"]));
		bool? bothOnForTheTool = null, masterFirstForTheTool = null;
		StepRegistry.Replace(new FakeStep("texgen", (ctx, s) => {
			bothOnForTheTool = ctx.Profile.IsOn("Base Out") && ctx.Profile.IsOn("Patch On Base");
			var inOrder = File.ReadAllLines(ctx.Profile.LoadOrderTxt).ToList();
			masterFirstForTheTool = inOrder.IndexOf("BaseOut.esp") >= 0 && inOrder.IndexOf("BaseOut.esp") < inOrder.IndexOf("PatchOnBase.esp");
			WriteOutput(ctx, "texgen", "Output C", "OutC.esp");
		}));
		var pair = g.WriteSetup(("synthesis", new JsonObject { ["output_mod"] = "Patch On Base" }), ("pandora", new JsonObject { ["output_mod"] = "Base Out" }),
			("texgen", new JsonObject { ["output_mod"] = "Output C" }));
		((JsonArray)pair.Root["steps"]!)[0]![OldOutput.Key] = true;
		((JsonArray)pair.Root["steps"]!)[1]![OldOutput.Key] = true;
		pair.Save();
		Equal("0,0", string.Join(",", OldOutput.Places(pair, pair.EnabledSteps).Select(x => x.At)), "(both come on before the same tool)");
		Equal("pandora,synthesis", string.Join(",", OldOutput.InMasterOrder(Mo2Instance.Load(g.Exe, g.Instance), pair.KeptSteps).Select(x => x.Key)), "the one whose plugin the other needs comes on first");
		gate = Runner.CheckStart(new RunRequest(g.SetupFile, g.Data));
		Check(gate.Starts, "two old outputs due before the same tool are judged together: " + gate.Why);
		var ran = Runner.Run(new RunRequest(g.SetupFile, g.Data));
		Equal(RunState.Finished, ran.State, ran.Verdict);
		Check(bothOnForTheTool == true, "the tool after them found both switched on");
		Check(masterFirstForTheTool == true, "with the master before the plugin that needs it in the load order");
		// ... and after the run: both were in no order file before it, so the put-back gives their plugins their ticks and
		// places again (the order files of before the run hold neither)
		var order = g.Text("loadorder.txt").Replace("\r\n", "\n").Split('\n').ToList();
		Check(order.IndexOf("BaseOut.esp") >= 0 && order.IndexOf("BaseOut.esp") < order.IndexOf("PatchOnBase.esp"), "after the run the master still stands before the plugin that needs it: " + string.Join(" | ", order));
		var ticked = ProfileOf(g).ActivePlugins();
		Check(ticked.Contains("BaseOut.esp", StringComparer.OrdinalIgnoreCase) && ticked.Contains("PatchOnBase.esp", StringComparer.OrdinalIgnoreCase) && ticked.Contains("OutC.esp", StringComparer.OrdinalIgnoreCase),
			"and their plugins, and the new output's, are ticked: " + string.Join(" | ", ticked));
		// two steps that share one output mod are two uses: the run's record and its summary name both
		var shared = g.WriteSetup(("synthesis", new JsonObject { ["output_mod"] = "Base Out" }), ("pandora", new JsonObject { ["output_mod"] = "Base Out" }),
			("texgen", new JsonObject { ["output_mod"] = "Output C" }));
		((JsonArray)shared.Root["steps"]!)[0]![OldOutput.Key] = true;
		((JsonArray)shared.Root["steps"]!)[1]![OldOutput.Key] = true;
		shared.Save();
		ran = Runner.Run(new RunRequest(g.SetupFile, g.Data));
		Equal(RunState.Finished, ran.State, ran.Verdict);
		var sharedRun = Journal.Load(ran.Folder!);
		Equal("pandora:Base Out,synthesis:Base Out", string.Join(",", sharedRun.UsedOld.Where(u => u.Done).Select(u => $"{u.Step}:{u.Mod}").Order()), "each step that used the shared output is on record");
		Check(RunSummary.Of(sharedRun).Lines().Any(l => l.Contains("Synthesis (Base Out)", StringComparison.Ordinal) && l.Contains("(Base Out)", StringComparison.Ordinal) && l.Split("(Base Out)").Length == 3),
			"and the summary names both: " + string.Join(" | ", RunSummary.Of(sharedRun).Lines()));

		// ... and when the one that is needed was in the mod list, switched off, and the other had no line at all: the put-back
		// brings one back still off and the other switched on - their plugins get their places in master order all the same,
		// in one pass (in two, the master was listed after the plugin that needs it)
		using var g2 = new FakeMo2();
		g2.Mod("Base Out", ("BaseOut.esp", false, []));
		g2.Mod("Patch On Base", ("PatchOnBase.esp", false, ["BaseOut.esp"]));
		ProfileOf(g2).EnsureMod("Base Out");
		var mixed = g2.WriteSetup(("synthesis", new JsonObject { ["output_mod"] = "Patch On Base" }), ("pandora", new JsonObject { ["output_mod"] = "Base Out" }),
			("texgen", new JsonObject { ["output_mod"] = "Output C" }));
		((JsonArray)mixed.Root["steps"]!)[0]![OldOutput.Key] = true;
		((JsonArray)mixed.Root["steps"]!)[1]![OldOutput.Key] = true;
		mixed.Save();
		ran = Runner.Run(new RunRequest(g2.SetupFile, g2.Data));
		Equal(RunState.Finished, ran.State, ran.Verdict);
		order = g2.Text("loadorder.txt").Replace("\r\n", "\n").Split('\n').ToList();
		Check(order.IndexOf("BaseOut.esp") >= 0 && order.IndexOf("BaseOut.esp") < order.IndexOf("PatchOnBase.esp"),
			"one listed and off, the other not listed: after the run the master stands before the plugin that needs it: " + string.Join(" | ", order));
		Check(ProfileOf(g2).IsOn("Base Out") && ProfileOf(g2).IsOn("Patch On Base"), "and both are switched on");

		// The old outputs due at one place are taken as one: when one of them cannot be used after all, none of that place is
		// put on the run's record or switched on. (Taken one by one, the first was carried out before the second was found
		// gone - and stayed switched on after the run.)
		using var g3 = new FakeMo2();
		g3.Mod("Base Out", ("BaseOut.esp", false, []));
		g3.Mod("Patch On Base", ("PatchOnBase.esp", false, ["BaseOut.esp"]));
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			File.Delete(Path.Combine(ctx.Mo2.ModDir("Patch On Base"), "PatchOnBase.esp"));
		}));
		var texGenRan = false;
		StepRegistry.Replace(new FakeStep("texgen", (ctx, s) => texGenRan = true));
		var half = g3.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("pandora", new JsonObject { ["output_mod"] = "Base Out" }),
			("synthesis", new JsonObject { ["output_mod"] = "Patch On Base" }), ("texgen", new JsonObject { ["output_mod"] = "Output C" }));
		((JsonArray)half.Root["steps"]!)[1]![OldOutput.Key] = true;
		((JsonArray)half.Root["steps"]!)[2]![OldOutput.Key] = true;
		half.Save();
		ran = Runner.Run(new RunRequest(g3.SetupFile, g3.Data));
		Equal(RunState.Failed, ran.State, ran.Verdict);
		// (stopped by that very look, and nothing else: the verdict names the old output that is gone, and the tool after the
		// place never ran)
		Check(ran.Verdict.Contains("there is none to use any more - \"Patch On Base\" is missing or holds nothing now", StringComparison.Ordinal), "the run stopped on the old output that is gone: " + ran.Verdict);
		Check(!texGenRan, "and the tool after that place did not run");
		Equal(0, Journal.Load(ran.Folder!).UsedOld.Count, "when one old output of a place cannot be used, none of that place is put on the run's record");
		Check(!ProfileOf(g3).States().ContainsKey("Base Out") && !ProfileOf(g3).States().ContainsKey("Patch On Base"), "and none of them is in the mod list or switched on after the run");

		// ... and when one of them cannot be switched on at its place after all (its line taken out of the mod list while the
		// run gave the other its line), none is carried out: the run stops there, and the put-back leaves both as the profile
		// had them
		using var g4 = new FakeMo2();
		g4.Mod("Base Out", ("BaseOut.esp", false, []));
		g4.Mod("Patch On Base", ("PatchOnBase.esp", false, ["BaseOut.esp"]));
		ProfileOf(g4).EnsureMod("Base Out");
		var g4Before = g4.Text("modlist.txt");
		var g4List = Path.Combine(g4.ProfileDir, "modlist.txt");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		texGenRan = false;
		var lineGone = g4.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("pandora", new JsonObject { ["output_mod"] = "Base Out" }),
			("synthesis", new JsonObject { ["output_mod"] = "Patch On Base" }), ("texgen", new JsonObject { ["output_mod"] = "Output C" }));
		((JsonArray)lineGone.Root["steps"]!)[1]![OldOutput.Key] = true;
		((JsonArray)lineGone.Root["steps"]!)[2]![OldOutput.Key] = true;
		lineGone.Save();
		Profile.AfterListMod = m => {
			if (m == "Patch On Base") {
				File.WriteAllText(g4List, File.ReadAllText(g4List).Replace("-Base Out\r\n", "", StringComparison.Ordinal));
			}
		};
		try {
			ran = Runner.Run(new RunRequest(g4.SetupFile, g4.Data));
		} finally {
			Profile.AfterListMod = null;
		}
		Equal(RunState.Failed, ran.State, ran.Verdict);
		Check(ran.Verdict.Contains("the old output \"Base Out\" could not be switched on: its line is no longer in the mod list", StringComparison.Ordinal), "the run stops on the old output it could not switch on: " + ran.Verdict);
		Equal("TR-322", RunnerErrors.Find(ran.Verdict)?.Code ?? "", "with what to do about it");
		Check(!texGenRan, "the tool after that place did not run");
		Check(Journal.Load(ran.Folder!).UsedOld.All(u => !u.Done), "none of the group is on record as carried out");
		Check(!ProfileOf(g4).IsOn("Patch On Base") && !ProfileOf(g4).ActivePlugins().Contains("PatchOnBase.esp", StringComparer.OrdinalIgnoreCase),
			"and the one that was switched on is not on after the run, its plugin not ticked");
		Check(ProfileOf(g4).States().TryGetValue("Base Out", out var baseFlag) && baseFlag == '-', "the other has its line back, off, as the profile had it: " + g4.Text("modlist.txt"));
		Equal(g4Before, g4.Text("modlist.txt"), "the mod list is as it was before the run");

		// What the player switches in Mod Organizer during the run counts for the profile the run's end leaves. A master's
		// mod they untick while an earlier tool works is off at the end: the old output that needs it is refused at its
		// place - also with no tool after it
		using var p1 = new FakeMo2();
		p1.Mod("Old Patch", ("OldPatch.esp", false, ["BaseMod.esp"]));
		var p1List = Path.Combine(p1.ProfileDir, "modlist.txt");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			File.WriteAllText(p1List, File.ReadAllText(p1List).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		}));
		var unticked = p1.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Old Patch" }));
		((JsonArray)unticked.Root["steps"]!)[1]![OldOutput.Key] = true;
		unticked.Save();
		ran = Runner.Run(new RunRequest(p1.SetupFile, p1.Data));
		Equal(RunState.Failed, ran.State, ran.Verdict);
		Check(ran.Verdict.Contains("its old output cannot be used: OldPatch.esp needs BaseMod.esp", StringComparison.Ordinal), "an old output whose master the player switched off during the run is refused at its place: " + ran.Verdict);
		Check(!ProfileOf(p1).IsOn("Old Patch") && !ProfileOf(p1).IsOn("Base Mod"), "it is not switched on, and what the player unticked stays unticked");
		// ... and one they untick after the old output is on (a later tool is at work): the put-back keeps what the player did,
		// leaves that old output switched off, and the run's notes say why - never on with its master off
		using var p2 = new FakeMo2();
		p2.Mod("Old Patch", ("OldPatch.esp", false, ["BaseMod.esp"]));
		var p2List = Path.Combine(p2.ProfileDir, "modlist.txt");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			File.WriteAllText(p2List, File.ReadAllText(p2List).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		}));
		var untickedLater = p2.WriteSetup(("synthesis", new JsonObject { ["output_mod"] = "Old Patch" }), ("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		((JsonArray)untickedLater.Root["steps"]!)[0]![OldOutput.Key] = true;
		untickedLater.Save();
		ran = Runner.Run(new RunRequest(p2.SetupFile, p2.Data));
		Equal(RunState.Finished, ran.State, ran.Verdict);
		Check(!ProfileOf(p2).IsOn("Old Patch") && !ProfileOf(p2).IsOn("Base Mod"), "the old output is left switched off, and what the player unticked stays unticked");
		Check(!ProfileOf(p2).ActivePlugins().Contains("OldPatch.esp", StringComparer.OrdinalIgnoreCase), "its plugin is not ticked");
		Check(Journal.Load(ran.Folder!).Notes.Any(n => n.Contains("\"Old Patch\" is left switched off", StringComparison.Ordinal) && n.Contains("BaseMod.esp", StringComparison.Ordinal)),
			"and the run's notes say why: " + string.Join(" | ", Journal.Load(ran.Folder!).Notes));

		// A master's mod switched on with that plugin unticked in the plugin list loads no master: at its place, with a tool
		// after it, the old output that needs it is refused - a master counts only while its plugin is ticked
		using var p3 = new FakeMo2();
		p3.Mod("Old Patch", ("OldPatch.esp", false, ["BaseMod.esp"]));
		var p3Plugins = Path.Combine(p3.ProfileDir, "plugins.txt");
		File.WriteAllText(p3Plugins, File.ReadAllText(p3Plugins).Replace("*BaseMod.esp", "BaseMod.esp", StringComparison.Ordinal));
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		var masterUnticked = p3.WriteSetup(("synthesis", new JsonObject { ["output_mod"] = "Old Patch" }), ("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		((JsonArray)masterUnticked.Root["steps"]!)[0]![OldOutput.Key] = true;
		masterUnticked.Save();
		ran = Runner.Run(new RunRequest(p3.SetupFile, p3.Data));
		Equal(RunState.Failed, ran.State, ran.Verdict);
		Check(ran.Verdict.Contains("OldPatch.esp needs BaseMod.esp, which is not switched on at this point of the run", StringComparison.Ordinal), "a master whose plugin is not ticked is not there for the tool after it: " + ran.Verdict);
		Check(!ProfileOf(p3).IsOn("Old Patch") && !ProfileOf(p3).ActivePlugins().Contains("OldPatch.esp", StringComparer.OrdinalIgnoreCase), "the old output is not switched on");

		// An old output whose mod the player switches on in Mod Organizer while an earlier tool works, its plugin left unticked:
		// at its place its plugin is ticked all the same - the tool after it finds the plugin it was promised
		// (only a mod that was off had its plugins ticked)
		using var p4 = new FakeMo2();
		p4.Mod("Kept", ("Kept.esp", false, []));
		ProfileOf(p4).EnsureMod("Kept");
		var p4List = Path.Combine(p4.ProfileDir, "modlist.txt");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			File.WriteAllText(p4List, File.ReadAllText(p4List).Replace("-Kept", "+Kept", StringComparison.Ordinal));
		}));
		bool? keptTickedForTheTool = null;
		StepRegistry.Replace(new FakeStep("texgen", (ctx, s) => {
			keptTickedForTheTool = ctx.Profile.ActivePlugins().Contains("Kept.esp", StringComparer.OrdinalIgnoreCase);
			WriteOutput(ctx, "texgen", "Output C", "OutC.esp");
		}));
		var onByThem = p4.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Kept" }),
			("texgen", new JsonObject { ["output_mod"] = "Output C" }));
		((JsonArray)onByThem.Root["steps"]!)[1]![OldOutput.Key] = true;
		onByThem.Save();
		ran = Runner.Run(new RunRequest(p4.SetupFile, p4.Data));
		Equal(RunState.Finished, ran.State, ran.Verdict);
		Check(keptTickedForTheTool == true, "the tool after its place found its plugin ticked");
		Check(ProfileOf(p4).IsOn("Kept") && ProfileOf(p4).ActivePlugins().Contains("Kept.esp", StringComparer.OrdinalIgnoreCase), "and after the run it is on, its plugin ticked");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => { }));

		// Plugins whose masters lie in each other's mods: "Mod A" holds ABase.esp and APatch.esp, "Mod B" holds B.esp; APatch.esp
		// needs B.esp, and B.esp needs ABase.esp. Mod by mod, either order lists a plugin before its master - plugin by plugin,
		// never
		using var po = new FakeMo2();
		po.Mod("Mod A", ("ABase.esp", false, []), ("APatch.esp", false, ["B.esp"]));
		po.Mod("Mod B", ("B.esp", false, ["ABase.esp"]));
		foreach (var given in new[] { new[] { "Mod A", "Mod B" }, new[] { "Mod B", "Mod A" } }) {
			File.WriteAllText(Path.Combine(po.ProfileDir, "plugins.txt"), "*BaseMod.esp\r\n");
			File.WriteAllText(Path.Combine(po.ProfileDir, "loadorder.txt"), "Skyrim.esm\r\nBaseMod.esp\r\n");
			ProfileOf(po).ActivatePlugins(given);
			var lo = po.Text("loadorder.txt").Replace("\r\n", "\n").Split('\n').ToList();
			Check(lo.IndexOf("ABase.esp") > 0 && lo.IndexOf("ABase.esp") < lo.IndexOf("B.esp") && lo.IndexOf("B.esp") < lo.IndexOf("APatch.esp"),
				$"given as {string.Join(", ", given)}: each master stands before what needs it: " + string.Join(" | ", lo));
			Check(po.Text("plugins.txt").Replace("\r\n", "\n").Split('\n').Where(l => l.StartsWith('*')).Select(l => l[1..]).SequenceEqual(["BaseMod.esp", "ABase.esp", "B.esp", "APatch.esp"]),
				"and ticked in that order: " + po.Text("plugins.txt"));
		}

		// The put-back never switches on a mod a later step of the run replaced for good - also not one the run had switched on
		// as an old output before that step - and an old output whose master is in such a mod is left off, and said
		// (the masters were judged by the profile as it stood before those mods went off)
		using var pr = new FakeMo2();
		pr.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		pr.Mod("Old Patch", ("OldPatch.esp", false, ["BaseMod.esp"]));
		var prc = StepContext(pr, "landscapefixer");
		try {
			prc.Profile.EnsureMod("Old Patch");
			prc.Profile.SetMods([], ["Output B", "Needs B"]);
			var prSnap = prc.Profile.Snapshot();
			prc.Journal.UsedOld.Add(new OldUse { Step = "pgpatcher", Name = "PGPatcher", Mod = "Output B", Done = true });
			prc.Journal.UsedOld.Add(new OldUse { Step = "synthesis", Name = "Synthesis", Mod = "Old Patch", Done = true });
			prc.Journal.Retire.AddRange(["Output B", "Base Mod"]);
			Runner.PutBack(prc, prSnap);
			Check(!ProfileOf(pr).IsOn("Output B"), "a used old output that a later step replaced stays off");
			Check(!ProfileOf(pr).IsOn("Base Mod"), "(the replaced mod that holds the master is off)");
			Check(!ProfileOf(pr).IsOn("Old Patch") && !ProfileOf(pr).ActivePlugins().Contains("OldPatch.esp", StringComparer.OrdinalIgnoreCase),
				"an old output whose master is in a mod that went off for good is left off");
			Check(prc.Journal.Notes.Any(n => n.Contains("\"Old Patch\" is left switched off", StringComparison.Ordinal) && n.Contains("BaseMod.esp", StringComparison.Ordinal)),
				"and the run's notes say why: " + string.Join(" | ", prc.Journal.Notes));
		} finally {
			prc.Log.Dispose();
		}

		// An old output that was switched on before the run gets its ticks back as they were, and the put-back ticks none of its
		// plugins: a master in that same mod counts only while its plugin is ticked. Unticked, the patch would be on with its
		// master off - the old output is left off and said. Ticked, it stays on
		// (and with both unticked - an optional patch and its optional master left off by the player - the mod's other plugin
		// that is ticked keeps it on: a plugin that stays unticked needs nothing)
		foreach (var (masterTicked, patchTicked) in new[] { (false, true), (true, true), (false, false) }) {
			using var ps = new FakeMo2();
			ps.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
			ps.Mod("Same Mod", ("SameBase.esp", false, []), ("SamePatch.esp", false, ["SameBase.esp"]), ("SameOther.esp", false, []));
			var psList = Path.Combine(ps.ProfileDir, "modlist.txt");
			File.WriteAllText(psList, File.ReadAllText(psList).Replace("+Base Mod", "+Same Mod\r\n+Base Mod", StringComparison.Ordinal));
			File.AppendAllText(Path.Combine(ps.ProfileDir, "plugins.txt"), (masterTicked ? "*" : "") + "SameBase.esp\r\n" + (patchTicked ? "*" : "") + "SamePatch.esp\r\n*SameOther.esp\r\n");
			File.AppendAllText(Path.Combine(ps.ProfileDir, "loadorder.txt"), "SameBase.esp\r\nSamePatch.esp\r\nSameOther.esp\r\n");
			var psc = StepContext(ps, "landscapefixer");
			try {
				var psSnap = psc.Profile.Snapshot();
				psc.Journal.UsedOld.Add(new OldUse { Step = "synthesis", Name = "Synthesis", Mod = "Same Mod", Done = true });
				Runner.PutBack(psc, psSnap);
				var psActive = ProfileOf(ps).ActivePlugins();
				if (!patchTicked) {
					Check(ProfileOf(ps).IsOn("Same Mod") && psActive.Contains("SameOther.esp", StringComparer.OrdinalIgnoreCase) && !psActive.Contains("SamePatch.esp", StringComparer.OrdinalIgnoreCase),
						"the patch and its master both unticked: the old output stays on as the player had it: " + string.Join(" | ", psActive));
					Check(psc.Journal.Notes.All(n => !n.Contains("\"Same Mod\" is left switched off", StringComparison.Ordinal)), "and is not said to be left off");
				} else if (masterTicked) {
					Check(ProfileOf(ps).IsOn("Same Mod") && psActive.Contains("SamePatch.esp", StringComparer.OrdinalIgnoreCase) && psActive.Contains("SameBase.esp", StringComparer.OrdinalIgnoreCase),
						"its master ticked in the same mod: the old output stays on, both plugins ticked: " + string.Join(" | ", psActive));
					Check(psc.Journal.Notes.All(n => !n.Contains("\"Same Mod\" is left switched off", StringComparison.Ordinal)), "and no note says otherwise");
				} else {
					Check(!ProfileOf(ps).IsOn("Same Mod") && !psActive.Contains("SamePatch.esp", StringComparer.OrdinalIgnoreCase),
						"its master unticked in the same mod: the old output is left off, its patch not ticked: " + string.Join(" | ", psActive));
					Check(psc.Journal.Notes.Any(n => n.Contains("\"Same Mod\" is left switched off", StringComparison.Ordinal) && n.Contains("SameBase.esp", StringComparison.Ordinal)),
						"and the run's notes say why: " + string.Join(" | ", psc.Journal.Notes));
				}
			} finally {
				psc.Log.Dispose();
			}
		}

		// A master the player unticks while the run waits for Mod Organizer to close at the old output's place - after the old
		// output was looked at, before its plugins are ticked: looked at again there, the run stops, nothing of it ticked or
		// carried out, and the tool after it never runs (the mod list's line is given in that wait,
		// which is where this test unticks the master)
		using var pw = new FakeMo2();
		pw.Mod("Old Patch", ("OldPatch.esp", false, ["BaseMod.esp"]));
		var pwPlugins = Path.Combine(pw.ProfileDir, "plugins.txt");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		var pwTexGen = false;
		StepRegistry.Replace(new FakeStep("texgen", (ctx, s) => pwTexGen = true));
		var inTheWait = pw.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Old Patch" }),
			("texgen", new JsonObject { ["output_mod"] = "Output C" }));
		((JsonArray)inTheWait.Root["steps"]!)[1]![OldOutput.Key] = true;
		inTheWait.Save();
		Profile.AfterListMod = m => {
			if (m == "Old Patch") {
				File.WriteAllText(pwPlugins, File.ReadAllText(pwPlugins).Replace("*BaseMod.esp", "BaseMod.esp", StringComparison.Ordinal));
			}
		};
		try {
			ran = Runner.Run(new RunRequest(pw.SetupFile, pw.Data));
		} finally {
			Profile.AfterListMod = null;
		}
		Equal(RunState.Failed, ran.State, ran.Verdict);
		Check(ran.Verdict.Contains("OldPatch.esp needs BaseMod.esp, which is not switched on at this point of the run", StringComparison.Ordinal), "a master unticked in the wait stops the run there: " + ran.Verdict);
		Check(!pwTexGen, "the tool after that place did not run");
		Check(Journal.Load(ran.Folder!).UsedOld.All(u => !u.Done), "nothing of it is on record as carried out");
		Check(!ProfileOf(pw).ActivePlugins().Contains("OldPatch.esp", StringComparer.OrdinalIgnoreCase), "and its plugin was never ticked");

		// a copy of a master in Mod Organizer's overwrite folder keeps it there when its mod goes off: what needs it stays on
		var deps = Mo2Instance.Load(f.Exe, f.Instance);
		Check(Profile.DependentsOf(deps, ProfileOf(f).States(), ["Output B"]).ContainsKey("Needs B"), "(a mod that needs a plugin of a mod going off goes off with it)");
		FakeMo2.Plugin(Path.Combine(f.Overwrite, "OutB.esp"), false);
		Check(!Profile.DependentsOf(deps, ProfileOf(f).States(), ["Output B"]).ContainsKey("Needs B"), "with a copy of that plugin in the overwrite folder it stays on");
		File.Delete(Path.Combine(f.Overwrite, "OutB.esp"));

		// a tool that uses its old output is still one of the setup's tools to set up in its own window
		var tools = Setup.FromJson(new JsonObject { ["mo2"] = new JsonObject { ["exe"] = f.Exe, ["profile"] = "Test" }, ["backups"] = f.Backups,
			["steps"] = new JsonArray(new JsonObject { ["step"] = "texgen", [OldOutput.Key] = true, ["settings"] = new JsonObject { ["output_mod"] = "Output B", ["executable"] = "TexGen" } },
				new JsonObject { ["step"] = "dyndolod", ["on"] = false, ["settings"] = new JsonObject { ["output_mod"] = "Output A", ["executable"] = "DynDOLOD" } }) }, f.SetupFile);
		Equal("texgen", string.Join(",", ToolSettings.ToSetUp(tools).Select(x => x.Step)), "a step that uses its old output is switched on: its tool is in the box that sets tools up, one that is switched off is not");
	}

	// The record of an old output that is used, and what a put-back makes of it. Nothing is
	// written for it before its place: a mod the list does not name gets its line there, once the run's record names it, and a
	// run that fails earlier leaves the mod list as it was. One that is gone by then stops the run before the tools after it.
	// And a put-back switches on only a use that was carried out, never a mod whose files are still in the run's backup
	static void T_AnOldOutputIsSwitchedOnOnlyOnceItsUseWasCarriedOut()
	{
		using var f = new FakeMo2();
		// an old output that Mod Organizer's list does not name (unpacked by hand, the list never refreshed)
		f.Mod("Old Patch", ("OldPatch.esp", false, ["BaseMod.esp"]));
		var fail = true;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			if (fail) {
				throw new StepFailedException("the tool gave up");
			}
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Old Patch" }));
		((JsonArray)setup.Root["steps"]!)[1]![OldOutput.Key] = true;
		setup.Save();
		var listBefore = f.Text("modlist.txt");
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, r.Verdict);
		Equal(listBefore, f.Text("modlist.txt"), "a run that failed before the old output's place leaves the mod list byte for byte as it was: no line was written for it");
		Equal(0, Journal.Load(r.Folder!).UsedOld.Count, "and its record names no old output as used");
		fail = false;
		r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		var j = Journal.Load(r.Folder!);
		Check(j.UsedOld.Single() is { Mod: "Old Patch", Listed: true, Done: true }, "used: on record as carried out, with the line the run gave it");
		Check(ProfileOf(f).IsOn("Old Patch"), "it is in the mod list now, and switched on");
		Check(ProfileOf(f).ActivePlugins().Contains("OldPatch.esp", StringComparer.OrdinalIgnoreCase), "with its plugin ticked (the plugin lists of before the run did not hold it)");
		Check(!j.Added.Contains("Old Patch") && File.Exists(Path.Combine(f.Mods, "Old Patch", "OldPatch.esp")), "it is not counted among the mods the run made: its folder is the player's, untouched");

		// A mod that is there as a folder with no line in the mod list, which a step of the run builds into - here it is also
		// the old output another step uses. The run gives it its line before its first step: it is one of the run's
		// outputs. A run that ends before anything was built into that mod or used of it takes the line out again - after a
		// step that failed, and after a crash once the profile is put back - so the mod list is as it was. The folder is
		// the player's: as it was
		using var k = new FakeMo2();
		k.Mod("Shared", ("Shared.esp", false, []));
		var how = "fail";
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			if (how == "crash") {
				// (what the run had on record by then: the mod it gave a line)
				ctx.Journal.Added.Add("Shared");
				ctx.Journal.Listed.Add("Shared");
				ctx.Journal.Pid = 999999;
				ctx.Journal.Save();
				throw new CrashForTest();
			}
			if (how == "fail") {
				throw new StepFailedException("the tool gave up");
			}
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		StepRegistry.Replace(new FakeStep("texgen", (ctx, s) => WriteOutput(ctx, "texgen", "Shared", "Shared.esp")));
		var both = k.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Shared" }),
			("texgen", new JsonObject { ["output_mod"] = "Shared" }));
		((JsonArray)both.Root["steps"]!)[1]![OldOutput.Key] = true;
		both.Save();
		var sharedBefore = k.Text("modlist.txt");
		var sharedBytes = File.ReadAllBytes(Path.Combine(k.Mods, "Shared", "Shared.esp"));
		r = Runner.Run(new RunRequest(k.SetupFile, k.Data));
		Equal(RunState.Failed, r.State, r.Verdict);
		Equal("Shared", string.Join(",", Journal.Load(r.Folder!).Listed), "(on record: the run gave that mod its line)");
		Equal(sharedBefore, k.Text("modlist.txt"), "a run that failed before it built into that mod or used it leaves the mod list byte for byte as it was");
		Check(File.ReadAllBytes(Path.Combine(k.Mods, "Shared", "Shared.esp")).SequenceEqual(sharedBytes), "and what the mod holds as it was");
		// cut off by a crash in its first step: the line the run wrote, and the order files it read after writing it
		how = "crash";
		ProfileOf(k).EnsureMod("Shared");
		try {
			RunUntilCrash(k);
		} catch (CrashForTest) {
		}
		Check(ProfileOf(k).States().ContainsKey("Shared"), "(after the crash the line the run wrote is still in the mod list)");
		Equal(RunState.Restored, Runner.Restore(Journal.Interrupted(Runner.RunsDir(k.Data))!).State, "the profile put back after the crash");
		Equal(sharedBefore, k.Text("modlist.txt"), "the put-back after a crash takes that line out again too");
		// the run that goes through uses the old output at its place and then builds into the mod: it keeps its line, on
		how = "";
		r = Runner.Run(new RunRequest(k.SetupFile, k.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		Check(ProfileOf(k).IsOn("Shared") && File.Exists(Path.Combine(k.Mods, "Shared", "new.txt")), "built into by the run, the mod keeps its line and is switched on");
		// ... and undone, that run leaves the list as it was before it, and the mod with what it held
		var undone = Runner.Undo(Journal.Load(r.Folder!));
		Equal(RunState.Restored, undone.State, undone.Verdict);
		Check(!ProfileOf(k).States().ContainsKey("Shared"), "undone, the mod has no line again");
		Check(File.ReadAllBytes(Path.Combine(k.Mods, "Shared", "Shared.esp")).SequenceEqual(sharedBytes) && !File.Exists(Path.Combine(k.Mods, "Shared", "new.txt")), "and holds what it held before the run");
		// The run puts that mod on record BEFORE it writes the line. With the mod list held by another program the line
		// cannot be written: the run stops before anything was changed, and its record already names the mod
		var listHeld = k.Text("modlist.txt");
		using (new FileStream(Path.Combine(k.ProfileDir, "modlist.txt"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
			r = Runner.Run(new RunRequest(k.SetupFile, k.Data));
		}
		Equal(RunState.Failed, r.State, r.Verdict);
		Check(r.Verdict.StartsWith("stopped before anything was changed", StringComparison.Ordinal), "a mod list that cannot be written stops the run before anything was changed: " + r.Verdict);
		Equal("Shared", string.Join(",", Journal.Load(r.Folder!).Listed), "the mod was on the run's record before its line was written");
		Equal(listHeld, k.Text("modlist.txt"), "and the mod list is as it was");
		// Cut off by a crash right after the run wrote that line and before it read the order files: all a put-back has is
		// the record the RUN itself had written by then. Both are taken as they stood at that moment - the run's record and
		// the mod list - and laid out again once this run is over: nothing of the record is written by the test but that
		// its process is gone. On a Mod Organizer of its own, where this run is the only one on record: the record taken is
		// that very run's (picked from several by their times, two made within one tick could be taken for each other)
		using var k2 = new FakeMo2();
		k2.Mod("Shared", ("Shared.esp", false, []));
		var both2 = k2.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Shared" }),
			("texgen", new JsonObject { ["output_mod"] = "Shared" }));
		((JsonArray)both2.Root["steps"]!)[1]![OldOutput.Key] = true;
		both2.Save();
		var k2Before = k2.Text("modlist.txt");
		byte[]? cutRecord = null, cutList = null;
		var runsThen = -1;
		Profile.AfterListMod = m => {
			if (m == "Shared" && cutRecord == null) {
				var runs = Directory.GetDirectories(Runner.RunsDir(k2.Data));
				runsThen = runs.Length;
				if (runs.Length == 1) {
					cutRecord = File.ReadAllBytes(Path.Combine(runs[0], "journal.json"));
					cutList = File.ReadAllBytes(Path.Combine(k2.ProfileDir, "modlist.txt"));
				}
			}
		};
		try {
			how = "fail";
			r = Runner.Run(new RunRequest(k2.SetupFile, k2.Data));
		} finally {
			Profile.AfterListMod = null;
		}
		Equal(RunState.Failed, r.State, r.Verdict);
		Equal(1, runsThen, "(the run that wrote the line is the only run on record)");
		Check(cutRecord != null && cutList != null, "(the run's record and the mod list were taken as the line was written)");
		var cutFolder = Path.Combine(Runner.RunsDir(k2.Data), "0002 cut off");
		Directory.CreateDirectory(cutFolder);
		File.WriteAllBytes(Path.Combine(cutFolder, "journal.json"), cutRecord!);
		File.WriteAllBytes(Path.Combine(k2.ProfileDir, "modlist.txt"), cutList!);
		var cut = Journal.Load(cutFolder);
		Equal("Shared", string.Join(",", cut.Listed), "the record the run had written before the line names the mod");
		cut.Pid = 999999;
		cut.Save();
		Check(ProfileOf(k2).States().ContainsKey("Shared"), "(the line is in the mod list, as the crash left it)");
		var afterCut = Runner.Restore(Journal.Load(cutFolder));
		Equal(RunState.Restored, afterCut.State, afterCut.Verdict);
		Equal(k2Before, k2.Text("modlist.txt"), "put back from the run's own record alone, the mod list is as it was");

		// A run that lists one output's line, and then meets the mod list held by another program - its next line cannot be
		// written, nor the first one taken out again - stops at the start with the put-back ON OFFER: the line it wrote is
		// still there. Once the list is free, "Put my profile back" takes the line out, and the mod's folder is as it was
		using var k3 = new FakeMo2();
		k3.Mod("Shared", ("Shared.esp", false, []));
		k3.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Shared" }), ("texgen", new JsonObject { ["output_mod"] = "Output New" }));
		var k3Before = k3.Text("modlist.txt");
		var k3Bytes = File.ReadAllBytes(Path.Combine(k3.Mods, "Shared", "Shared.esp"));
		FileStream? k3Held = null;
		Profile.AfterListMod = m => {
			if (m == "Shared" && k3Held == null) {
				k3Held = new FileStream(Path.Combine(k3.ProfileDir, "modlist.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
			}
		};
		try {
			r = Runner.Run(new RunRequest(k3.SetupFile, k3.Data));
		} finally {
			Profile.AfterListMod = null;
			k3Held?.Dispose();
		}
		Equal(RunState.Failed, r.State, r.Verdict);
		Check(r.Verdict.Contains("the profile is back except the line this run gave \"Shared\" in the mod list", StringComparison.Ordinal), "the verdict names the line that is still there: " + r.Verdict);
		Equal("TR-401", RunnerErrors.Find(r.Verdict)?.Code ?? "", "with what to do about it");
		var k3Run = Journal.Load(r.Folder!);
		Check(k3Run.NeedsRestore && k3Run.Unfinished, "the put-back stays on offer");
		Check(ProfileOf(k3).States().ContainsKey("Shared"), "(the line the run wrote is still in the mod list)");
		Check(Runner.CheckStart(new RunRequest(k3.SetupFile, k3.Data)) is { Starts: false } k3Gate && k3Gate.Why.Contains("did not finish", StringComparison.Ordinal), "and no new run starts over it");
		var k3Back = Runner.Restore(Journal.Interrupted(Runner.RunsDir(k3.Data))!);
		Equal(RunState.Restored, k3Back.State, k3Back.Verdict);
		Equal(k3Before, k3.Text("modlist.txt"), "the list free again, the put-back takes the line out: the mod list is as it was");
		Check(File.ReadAllBytes(Path.Combine(k3.Mods, "Shared", "Shared.esp")).SequenceEqual(k3Bytes), "and the mod's folder holds what it held");
		// A folder that was there before the run with nothing in it but a meta.ini this program once wrote (an earlier run's
		// empty output, its line gone): the run gives it a line, fails before its tool - and only the line goes again. The
		// folder is not the run's to move away
		var emptyOne = Path.Combine(k.Mods, "Shared Empty");
		Directory.CreateDirectory(emptyOne);
		File.WriteAllText(Path.Combine(emptyOne, "meta.ini"), $"[General]\r\ncomments=made by {Brand.Name}\r\n");
		Check(Profile.IsEmptyOutput(emptyOne), "(it reads as an empty output of this program's)");
		k.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("texgen", new JsonObject { ["output_mod"] = "Shared Empty" }));
		var emptyBefore = k.Text("modlist.txt");
		how = "fail";
		r = Runner.Run(new RunRequest(k.SetupFile, k.Data));
		Equal(RunState.Failed, r.State, r.Verdict);
		Check(File.Exists(Path.Combine(emptyOne, "meta.ini")), "an empty folder that was there before the run is still there after a run that failed");
		Equal(emptyBefore, k.Text("modlist.txt"), "and its new line is gone again");

		// A line the put-back cannot take out again (the mod list held by another program for a moment) is named as not back -
		// the run and "Put my profile back" then stay on offer -, and the next put-back takes it out
		using var q = new FakeMo2();
		q.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		q.Mod("Loose", ("Loose.esp", false, []));
		var held = StepContext(q, "landscapefixer");
		try {
			var looseBefore = q.Text("modlist.txt");
			held.Profile.EnsureMod("Loose");
			var looseSnap = held.Profile.Snapshot();
			held.Journal.Added.Add("Loose");
			held.Journal.Listed.Add("Loose");
			using (new FileStream(Path.Combine(q.ProfileDir, "modlist.txt"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
				Runner.PutBack(held, looseSnap);
			}
			Equal("Loose", string.Join(",", held.LinesLeft), "a line that could not be taken out is named as not back");
			Check(ProfileOf(q).States().ContainsKey("Loose"), "(it is still in the mod list)");
			Runner.PutBack(held, looseSnap);
			Check(held.LinesLeft.Count == 0, "the next put-back takes it out");
			Equal(looseBefore, q.Text("modlist.txt"), "and the mod list is as it was before the run wrote the line");
		} finally {
			held.Log.Dispose();
		}

		// gone by the time its place comes (a tool, or a command of the setup, moved it away after the check): the run stops
		// there, and the tool after it never runs on an output that is not there
		using var g = new FakeMo2();
		var ranAfter = false;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			File.Delete(Path.Combine(ctx.Mo2.ModDir("Output B"), "OutB.esp"));
		}));
		StepRegistry.Replace(new FakeStep("texgen", (ctx, s) => ranAfter = true));
		var gone = g.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("pgpatcher", new JsonObject { ["output_mod"] = "Output B" }),
			("texgen", new JsonObject { ["output_mod"] = "Output C" }));
		((JsonArray)gone.Root["steps"]!)[1]![OldOutput.Key] = true;
		gone.Save();
		r = Runner.Run(new RunRequest(g.SetupFile, g.Data));
		Equal(RunState.Failed, r.State, r.Verdict);
		Check(!ranAfter, "the tool after its place did not run");
		Check(r.Verdict.Contains("there is none to use any more - \"Output B\" is missing or holds nothing now", StringComparison.Ordinal), "the run says why it stopped: " + r.Verdict);
		Equal("TR-320", RunnerErrors.Find(r.Verdict)?.Code ?? "", "with what to do about it");
		Equal(0, Journal.Load(r.Folder!).UsedOld.Count, "nothing is on record as used");
		Check(ProfileOf(g).IsOn("Output A"), "what the run had built before it is on");

		// the put-back: a use the run was only about to make switches nothing on; one it carried out is on, like what was built
		using var h = new FakeMo2();
		h.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var ctx = StepContext(h, "landscapefixer");
		try {
			// before the run the old output was switched off; the run was cut off with its use on record and nothing switched
			// (a Stop while Mod Organizer closed for it, a crash after the record was written)
			ctx.Profile.SetMods([], ["Needs B", "Output B"]);
			var snap = ctx.Profile.Snapshot();
			ctx.Journal.UsedOld.Add(new OldUse { Step = "synthesis", Name = "Synthesis", Mod = "Output B" });
			// (The ask to switch that old output on was still standing when the run was cut off - the read of the mod list that
			// follows it had failed, say - and so was one for a mod some step had asked for and never got. No record calls
			// either built or used, so the put-back switches neither on: it goes by its records, never by a standing ask)
			ctx.SetAfter("Output B", "Needs B");
			Runner.PutBack(ctx, snap);
			Check(!ProfileOf(h).IsOn("Output B"), "a use that was never carried out leaves the old output as the profile had it before the run");
			Check(!ProfileOf(h).IsOn("Needs B"), "and what was only asked for, on no record as built or used, is not switched on by the put-back");
			ctx.Journal.UsedOld[0].Done = true;
			Runner.PutBack(ctx, snap);
			Check(ProfileOf(h).IsOn("Output B"), "one that was carried out is switched on by the put-back");
			// a later step moved that mod's files into the run's backup and could not bring them back (two steps can share an
			// output mod): the mod stays off - it never goes on holding nothing
			ctx.Journal.Parked.Add(new ParkRecord { Step = "texgen", Mod = "Output B", From = h.Mods, To = Path.Combine(h.Backups, "not back") });
			Runner.PutBack(ctx, snap);
			Check(!ProfileOf(h).IsOn("Output B"), "while its files are still in the backup, the old output is not switched on");
		} finally {
			ctx.Log.Dispose();
		}
	}

	// The warning is the tool's own: every tool that makes an output says what an old one of ITS kind does in the game, and the
	// program adds what it can work out - when it was made, what changed in the profile since, which tools of this run it was made
	// before, which build on it. START's preview and the command line's report carry it
	static void T_TheOldOutputWarningSaysWhatItMeansForThatTool()
	{
		var general = OldOutput.Of("addon:somebody's patcher");
		foreach (var s in StepCatalog.All.Where(s => OldOutput.MakesOutput(s.Key))) {
			var m = OldOutput.Of(s.Key);
			Check(m != general && m.Holds.Length > 30 && m.WhenOld.Length > 60 && m.WhenOld.EndsWith('.') && !m.Holds.EndsWith('.'), $"{s.Name} has words of its own for what its old output holds and does");
			Check(!(m.Holds + m.WhenOld).Contains("colour", StringComparison.OrdinalIgnoreCase), $"{s.Name}: color, as everywhere");
		}
		Check(general.WhenOld.Length > 20, "an add-on's output gets the general words");
		var made = new DateTime(2026, 10, 27, 21, 52, 0, DateTimeKind.Local);
		var look = new OldOutput.Look("dyndolod", "DynDOLOD", "DynDOLOD Output", true, false, made, true,
			[("Blubbos Trees", "added"), ("Skyland AIO", "ticked"), ("Old Town", "removed"), ("Fourth", "added")], ["xLODGen", "TexGen"], [], null);
		var w = OldOutput.Warning(look, "My Profile");
		Check(w.Any(l => l.StartsWith("Made Tue Oct 27 21:52 by a run of this setup", StringComparison.Ordinal)), "when it was made: " + string.Join(" | ", w));
		Check(w.Any(l => l.StartsWith("4 changes in the profile My Profile since then (2 added, 1 removed, 1 ticked), e.g. Blubbos Trees added; Skyland AIO ticked; Old Town removed - none of them is in it", StringComparison.Ordinal)), "what changed since, counted and named");
		Check(w.Any(l => l.StartsWith("Built again in this run before its place: xLODGen, TexGen. This old output was made from what those tools made before, not from what they make now", StringComparison.Ordinal)), "the tools of this run it no longer fits");
		Check(w.Any(l => l.StartsWith("In the game: ", StringComparison.Ordinal) && l.Contains("the distance shows things that are not there up close", StringComparison.Ordinal)), "what the player sees, for this tool");
		Check(w.Any(l => l.Contains("the settings its tool had then", StringComparison.Ordinal)) && w.Any(l => l.Contains("switched off in Mod Organizer now: the run switches it on", StringComparison.Ordinal)), "its old settings, and that the run switches it on");
		var unknown = OldOutput.Warning(look with { ChangedSince = null, ByRun = false, BuiltAgainBefore = [], BuiltOnIt = ["DynDOLOD"], On = true, Step = "texgen", Name = "TexGen" }, "My Profile");
		Check(unknown.Any(l => l.Contains("no run of this setup on record built it", StringComparison.Ordinal)) && unknown.Any(l => l.StartsWith("What changed in your mods since then is not known", StringComparison.Ordinal)), "what is not known is said as not known");
		Check(unknown.Any(l => l.StartsWith("Built on top of it in this run: DynDOLOD - it reads this old output", StringComparison.Ordinal)), "and the tool that builds on it");

		using var f = new FakeMo2();
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("pgpatcher", new JsonObject { ["output_mod"] = "Output B" }),
			("texgen", new JsonObject { ["output_mod"] = "Output C" }));
		((JsonArray)setup.Root["steps"]!)[1]![OldOutput.Key] = true;
		setup.Save();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var ran = new LastRuns.Ran(made, "", "PGPatcher", StepState.Done, made, made.AddMinutes(9), "", "Output B");
		var preview = RunPreview.Build(setup, mo2, setup.EnabledSteps, [new LastRuns.Section("pgpatcher", ran, ran, [("Some Texture Mod", "added")], "Output B")]);
		var only = preview.UsesOld.Single();
		Check(only is { Step: "pgpatcher", Mod: "Output B", There: true, On: true, ByRun: true, ChangedSince.Count: 1 } && only.Made == made.AddMinutes(9), "the step as it stands, dated by the run that made its output");
		Equal("Landscape Fixer", string.Join(",", only.BuiltAgainBefore), "built again before it in this run");
		Equal("TexGen", string.Join(",", only.BuiltOnIt), "built on it in this run");
		var lines = preview.Lines(_ => null);
		Check(lines.Any(l => l.StartsWith("NOT built again - the old output is used", StringComparison.Ordinal)) && lines.Any(l => l == "  - PGPatcher: \"Output B\"") &&
		      lines.Any(l => l.Contains("1 change in the profile Test since then (1 added), e.g. Some Texture Mod added - it is not in it", StringComparison.Ordinal)) &&
		      lines.Any(l => l.Contains("In the game: A texture or mesh mod added or updated since is not patched", StringComparison.Ordinal)), "START's preview carries the warning: " + string.Join(" | ", lines));
		Check(lines.Count(l => l.Contains("remakes \"", StringComparison.Ordinal)) == 1 && lines.All(l => !l.Contains("PGPatcher - remakes", StringComparison.Ordinal)), "and never lists the step among the tools that run");
		Check(preview.OffForTheRun.Any(o => o.Mod == "Output B" && o.Why.Contains("the old output of PGPatcher, used again: back on at that step's place", StringComparison.Ordinal)), "the old output is among what goes off for the run, with why");
		var json = JsonNode.Parse(Reports.Text(Reports.Preview(setup, preview, _ => null)))!;
		Check(json["usesOldOutput"]![0]!["key"]!.ToString() == "pgpatcher" && json["usesOldOutput"]![0]!["usable"]!.GetValue<bool>() && json["usesOldOutput"]![0]!["changesSince"]!.GetValue<int>() == 1, "the command line's report names it too");
		// without a run on record its files date it, and what changed since is not known
		var plain = OldOutput.LookAt(setup, mo2, ProfileOf(f).States(), setup.EnabledSteps).Single();
		Check(plain is { ByRun: false, ChangedSince: null, Made: not null }, "no run on record: dated by its files");
		Equal(1, OldOutput.Places(setup, setup.EnabledSteps).Single().At, "its place: before the first step of the run that stands after it");
		Equal(0, OldOutput.Places(setup, setup.EnabledSteps.Where(x => x.Key == "texgen").ToList()).Single().At, "in a run of only a later step: before that step");
		Equal(1, OldOutput.Places(setup, setup.EnabledSteps.Where(x => x.Key == "landscapefixer").ToList()).Single().At, "in a run of only an earlier step: after it");
	}

	// A change of several fields of the run's record made as one is written whole: a save on another thread (the run's box
	// watch saves the record too) waits until the whole change is made. Written half way, a crash right then left half of a
	// group of old outputs carried out
	static void T_AChangeOfTheRunsRecordIsWrittenWhole()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "record " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		// (the worker's own failure is kept and said here: one left to its thread would end the whole test run)
		Thread? other = null;
		Exception? failed = null;
		try {
			var j = new Journal { Folder = dir, UsedOld = [new OldUse { Step = "pandora", Mod = "Base Out" }, new OldUse { Step = "synthesis", Mod = "Patch On Base" }] };
			j.Save();
			// A thread of its own (a pool task may not have started yet) that says when it is about to save; the change goes on
			// only once Windows has put that thread to sleep on the record's lock - so the save it races is really under way
			bool reached = false, blocked = false, stillWaiting = false;
			using var started = new ManualResetEventSlim();
			j.SaveWith(() => {
				j.UsedOld[0].Done = true;
				other = new Thread(() => {
					started.Set();
					try {
						j.Save();
					} catch (Exception e) when (e is not OutOfMemoryException) {
						failed = e;
					}
				}) { IsBackground = true };
				other.Start();
				reached = started.Wait(TimeSpan.FromSeconds(10));
				for (var until = Environment.TickCount64 + 10_000; reached && !blocked && other.IsAlive && Environment.TickCount64 < until;) {
					blocked = (other.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0;
					if (!blocked) {
						Thread.Sleep(5);
					}
				}
				Thread.Sleep(100);
				stillWaiting = other.IsAlive;
				j.UsedOld[1].Done = true;
			});
			Check(reached, "the other thread reached its save");
			Check(blocked && stillWaiting, "a save on another thread waits while the change is made");
			Check(other != null && other.Join(TimeSpan.FromSeconds(10)), "and goes on once it is done");
			Check(failed == null, "the other thread's save went through: " + failed?.Message);
			Check(Journal.Load(dir).UsedOld.All(u => u.Done), "what is on disk holds the whole change, never half of it");
		} finally {
			// the worker is done with the folder before it goes
			other?.Join(TimeSpan.FromSeconds(10));
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}
	}

	// A mod list in the PC's own code page cannot hold every name - one it would
	// write as "?" is refused and nothing is written; a name it can hold goes in as before. And several new plugins placed
	// after one anchor keep their master order in both plugin files
	static void T_TheModListRefusesANameItCannotHold()
	{
		using var f = new FakeMo2();
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		var ansi = Files.AnsiEncoding;
		// a line with a letter in that code page that is no valid UTF-8 makes the list read as written in it
		var accent = "Mod été";
		// (a code page that holds the name this test expects refused - CP936 on Simplified Chinese Windows - writes it rightly)
		var refused = "漢字 Output";
		if (ansi.GetString(ansi.GetBytes(accent)) != accent || ansi.GetString(ansi.GetBytes(refused)) == refused || ansi.CodePage == 65001) {
			Console.WriteLine("  (skipped: this PC's code page cannot hold the accented name, or can hold the refused one)");
			return;
		}
		File.WriteAllBytes(list, ansi.GetBytes(File.ReadAllText(list).Replace("+Base Mod", "+" + accent + "\r\n+Base Mod", StringComparison.Ordinal)));
		var before = File.ReadAllBytes(list);
		var p = ProfileOf(f);
		Throws<IOException>(() => p.ListMod(refused), "a name the list's code page cannot hold is refused");
		Check(File.ReadAllBytes(list).SequenceEqual(before), "and nothing was written");
		Check(p.ListMod("Café Output") && p.States().ContainsKey("Café Output"), "a name it can hold goes in and is found again");
		Check(p.States().ContainsKey(accent), "the line that was there is read as before");
	}

	// ... and several new plugins placed after one anchor keep their master order in both plugin files (a test of its own:
	// nothing of a PC's code page may skip it)
	static void T_OneAnchorKeepsTheMasterOrder()
	{
		using var f = new FakeMo2();
		// two new plugins after one anchor, the master given first: both files keep master, then the plugin that needs it
		f.Mod("Pair", ("PairBase.esp", false, []), ("PairPatch.esp", false, ["PairBase.esp"]));
		var anchor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PairBase.esp"] = "BaseMod.esp", ["PairPatch.esp"] = "BaseMod.esp" };
		ProfileOf(f).ActivatePlugins(["Pair"], anchor);
		foreach (var file in new[] { "loadorder.txt", "plugins.txt" }) {
			var lines = f.Text(file).Replace("\r\n", "\n").Split('\n').Select(l => l.TrimStart('*')).ToList();
			var at = lines.IndexOf("BaseMod.esp");
			Check(at >= 0 && lines.IndexOf("PairBase.esp") == at + 1 && lines.IndexOf("PairPatch.esp") == at + 2, $"{file}: right after the anchor, the master first: " + string.Join(" | ", lines));
		}
	}

	// A setup copied keeps the first as it was and takes the settings its tools learned along; renamed, its run
	// records and learned settings go with it (History, the last runs and Undo find a setup by its file); a name that cannot be a
	// file, or is taken, is refused in words; a run that did not finish refuses a rename and nothing moves
	static void T_ASetupIsCopiedAndRenamedWithWhatIsItsOwn()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("texgen", new JsonObject { ["output_mod"] = "Output A" }));
		var setups = Path.GetDirectoryName(f.SetupFile)!;
		var learned = Path.Combine(setups, "learned settings", "Test", "texgen");
		Directory.CreateDirectory(learned);
		File.WriteAllText(Path.Combine(learned, "learned.json"), "{}");
		var before = File.ReadAllBytes(f.SetupFile);
		Equal<string?>(null, SetupFiles.BadName(setups, "My Copy"), "a plain name is good");
		Check(SetupFiles.BadName(setups, "a/b") != null && SetupFiles.BadName(setups, " ") != null && SetupFiles.BadName(setups, "CON") != null && SetupFiles.BadName(setups, "x.") != null,
			"a name that cannot be a file is refused");
		Check(SetupFiles.BadName(setups, "test")?.Contains("is there already", StringComparison.Ordinal) == true, "a name taken (in other letters too) is refused");
		Equal<string?>(null, SetupFiles.BadName(setups, "TEST", f.SetupFile), "its own name in other letters is no clash for a rename");
		Check(SetupFiles.BadName(setups, "output sizes") != null, "the program's own files are no names");
		Equal("Test (copy)", SetupFiles.FreeCopyName(f.SetupFile), "a copy's first name");
		// (the setup carries its own name, "Test", after its file: the copy carries its new name, the rest as it is)
		string Without(string a_file, out string? a_name)
		{
			var o = (JsonObject)JsonNode.Parse(File.ReadAllText(a_file))!;
			a_name = o["name"]?.GetValue<string>();
			o.Remove("name");
			return o.ToJsonString();
		}
		var asItWas = Without(f.SetupFile, out _);
		var copy = SetupFiles.Duplicate(f.SetupFile, "Test (copy)");
		Check(Without(copy, out var copyName) == asItWas && copyName == "Test (copy)", "the copy holds the setup as it is, under its new name: " + copyName);
		Check(File.ReadAllBytes(f.SetupFile).SequenceEqual(before), "and the first is untouched");
		Check(File.Exists(Path.Combine(setups, "learned settings", "Test (copy)", "texgen", "learned.json")) && File.Exists(Path.Combine(learned, "learned.json")),
			"the learned settings are copied, the first keeps its own");
		Equal("Test (copy 2)", SetupFiles.FreeCopyName(f.SetupFile), "a second copy takes the next name");
		Throws<ArgumentException>(() => SetupFiles.Duplicate(f.SetupFile, "Test (copy)"), "a copy never writes over a setup that is there");
		// a setup that carries a name of its own after its file keeps doing so
		var named = Path.Combine(setups, "Named.json");
		File.WriteAllText(named, "{ \"name\": \"Named\" }");
		Equal("Named 2", JsonNode.Parse(File.ReadAllText(SetupFiles.Duplicate(named, "Named 2")))!["name"]!.GetValue<string>(), "a name inside it follows the new name");
		// renamed: its run record follows, another setup's does not
		var runs = Runner.RunsDir(f.Data);
		var mine = Path.Combine(runs, "2026-01-01 00-00-00 Test");
		var others = Path.Combine(runs, "2026-01-02 00-00-00 Test (copy)");
		Directory.CreateDirectory(mine);
		Directory.CreateDirectory(others);
		new Journal { Folder = mine, SetupFile = f.SetupFile, State = RunState.Finished }.Save();
		new Journal { Folder = others, SetupFile = copy, State = RunState.Finished }.Save();
		var done = SetupFiles.Rename(f.SetupFile, "Main Run", runs);
		Check(!File.Exists(f.SetupFile) && File.Exists(done.File) && Without(done.File, out var renamedName) == asItWas && renamedName == "Main Run",
			"the file has its new name and the same setup, its name inside following");
		Equal(1, done.Records, "one run record named it");
		Check(Files.SamePath(Journal.Load(mine).SetupFile, done.File) && Files.SamePath(Journal.Load(others).SetupFile, copy), "its own run record names the new file, another setup's is untouched");
		Check(Directory.Exists(Path.Combine(setups, "learned settings", "Main Run", "texgen")) && !Directory.Exists(Path.Combine(setups, "learned settings", "Test")), "its learned settings went with it");
		// the same name in other letters only
		var cased = SetupFiles.Rename(done.File, "MAIN RUN", runs);
		Check(Directory.GetFiles(setups, "*.json").Any(x => Path.GetFileName(x) == "MAIN RUN.json"), "a rename to other letters only takes");
		Check(Directory.GetDirectories(Path.Combine(setups, "learned settings")).Any(d => Path.GetFileName(d) == "MAIN RUN"), "and its learned settings folder too");
		// learned settings that cannot be moved (a file of them held open): the setup keeps its old name, the two never part
		var heldOpen = Path.Combine(setups, "learned settings", "MAIN RUN", "texgen", "learned.json");
		using (new FileStream(heldOpen, FileMode.Open, FileAccess.Read, FileShare.None)) {
			Throws<IOException>(() => SetupFiles.Rename(cased.File, "Locked Away", runs), "learned settings that cannot be moved stop the rename");
		}
		Check(File.Exists(cased.File) && !File.Exists(Path.Combine(setups, "Locked Away.json")) && File.Exists(heldOpen), "the file is back under its old name, its learned settings where they were");
		// a copy onto a name whose learned settings are left there by a setup that is gone is refused, nothing made
		Directory.CreateDirectory(Path.Combine(setups, "learned settings", "Leftover", "texgen"));
		Throws<ArgumentException>(() => SetupFiles.Duplicate(cased.File, "Leftover"), "a copy onto leftover learned settings is refused");
		Check(!File.Exists(Path.Combine(setups, "Leftover.json")), "and no copy is left");
		// Leftover learned settings refuse a copy also of a setup that has learned none
		Directory.CreateDirectory(Path.Combine(setups, "learned settings", "Named 3", "texgen"));
		Throws<ArgumentException>(() => SetupFiles.Duplicate(named, "Named 3"), "a copy of a setup without learned settings onto leftover ones is refused");
		Check(!File.Exists(Path.Combine(setups, "Named 3.json")), "and no copy is left");
		// a copy whose learned settings stop half way leaves neither the setup nor the part already copied
		var half = Path.Combine(setups, "Half.json");
		File.WriteAllText(half, "{ \"name\": \"Half\" }");
		var halfLearned = Path.Combine(setups, "learned settings", "Half", "texgen");
		Directory.CreateDirectory(halfLearned);
		File.WriteAllText(Path.Combine(halfLearned, "a.ini"), "a=1");
		File.WriteAllText(Path.Combine(halfLearned, "b.ini"), "b=1");
		using (new FileStream(Path.Combine(halfLearned, "b.ini"), FileMode.Open, FileAccess.Read, FileShare.None)) {
			Throws<IOException>(() => SetupFiles.Duplicate(half, "Half 2"), "a copy whose learned settings cannot all be read");
		}
		Check(!File.Exists(Path.Combine(setups, "Half 2.json")) && !Directory.Exists(Path.Combine(setups, "learned settings", "Half 2")), "leaves no setup and no part of its learned settings");
		Check(SetupFiles.Duplicate(half, "Half 2") is { } again && File.Exists(again), "so the same copy can be made again");
		// a rename whose name inside cannot be written: the file and its learned settings are back under the old name
		var broken = Path.Combine(setups, "Broken.json");
		File.WriteAllText(broken, "{ \"name\": \"Broken\", ");
		Directory.CreateDirectory(Path.Combine(setups, "learned settings", "Broken", "texgen"));
		Throws<IOException>(() => SetupFiles.Rename(broken, "Mended", runs), "a setup whose file does not read is not renamed half way");
		Check(File.Exists(broken) && !File.Exists(Path.Combine(setups, "Mended.json")) && Directory.Exists(Path.Combine(setups, "learned settings", "Broken", "texgen")) &&
			!Directory.Exists(Path.Combine(setups, "learned settings", "Mended")), "the file and its learned settings keep the old name");
		// a setup written by hand (a comment, a comma after its last value) is renamed as every setup reads: its name inside follows
		var loose = Path.Combine(setups, "Loose.json");
		File.WriteAllText(loose, "{\n  // made by hand\n  \"name\": \"Loose\",\n}");
		var tidy = SetupFiles.Rename(loose, "Tidy", runs).File;
		Equal("Tidy", JsonNode.Parse(File.ReadAllText(tidy))!["name"]!.GetValue<string>(), "a setup written by hand keeps its own name in step");
		// leftover learned settings under the new name refuse a rename also of a setup that has learned none
		Directory.CreateDirectory(Path.Combine(setups, "learned settings", "Taken", "texgen"));
		Throws<InvalidOperationException>(() => SetupFiles.Rename(tidy, "Taken", runs), "a rename onto leftover learned settings is refused");
		Check(File.Exists(tidy) && !File.Exists(Path.Combine(setups, "Taken.json")), "and nothing was renamed");
		// a run record that does not read may be an unfinished run's: nothing is renamed until it reads
		var unread = Path.Combine(runs, "2026-01-03 00-00-00 Test");
		Directory.CreateDirectory(unread);
		File.WriteAllText(Path.Combine(unread, "journal.json"), "{ not a record");
		Throws<InvalidOperationException>(() => SetupFiles.Rename(cased.File, "Later", runs), "a run record that does not read refuses a rename");
		Check(File.Exists(cased.File) && !File.Exists(Path.Combine(setups, "Later.json")), "and nothing was renamed");
		Directory.Delete(unread, true);
		// A run record that cannot be written after others were - every record names the old file
		// again and the setup keeps its old name, its name inside and its learned settings, so no run loses its setup in History
		var first = Path.Combine(runs, "2026-01-04 00-00-00 Test");
		var second = Path.Combine(runs, "2026-01-05 00-00-00 Test");
		Directory.CreateDirectory(first);
		Directory.CreateDirectory(second);
		new Journal { Folder = first, SetupFile = cased.File, State = RunState.Finished }.Save();
		new Journal { Folder = second, SetupFile = cased.File, State = RunState.Finished }.Save();
		using (new FileStream(Path.Combine(second, "journal.json"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
			Throws<IOException>(() => SetupFiles.Rename(cased.File, "Later", runs), "a run record that cannot be written stops the rename");
		}
		Check(File.Exists(cased.File) && !File.Exists(Path.Combine(setups, "Later.json")) && Without(cased.File, out var keptName) == asItWas && keptName == "MAIN RUN",
			"the setup keeps its old name, inside too");
		Check(new[] { mine, first, second }.All(d => Files.SamePath(Journal.Load(d).SetupFile, cased.File)), "every run record names it, also those written before the one that would not be");
		Check(Directory.Exists(Path.Combine(setups, "learned settings", "MAIN RUN", "texgen")) && !Directory.Exists(Path.Combine(setups, "learned settings", "Later")),
			"its learned settings are where they were");
		Directory.Delete(first, true);
		Directory.Delete(second, true);
		// a run that did not finish refuses it
		new Journal { Folder = mine, SetupFile = cased.File, State = RunState.Running }.Save();
		Throws<InvalidOperationException>(() => SetupFiles.Rename(cased.File, "Later", runs), "a run that did not finish refuses a rename");
		Check(File.Exists(cased.File) && !File.Exists(Path.Combine(setups, "Later.json")), "and nothing was renamed");
	}

	// The log window's search goes on from where it stands and from the top again; "first problem" is the first
	// line the run marked as a failure, else the first it marked as a warning
	static void T_TheLogWindowFindsTheNextAndTheFirstProblem()
	{
		var log = "21:00:01  Tool Runner - Test\n21:00:02  ! a warning first\n21:00:03    a detail\n21:00:04  !!! the step failed\n21:00:05  done\n";
		Equal(log.IndexOf("21:00:04", StringComparison.Ordinal), ToolRunner.App.LogSearch.FirstProblem(log), "a failure line wins over an earlier warning");
		Equal(log.IndexOf("21:00:02", StringComparison.Ordinal), ToolRunner.App.LogSearch.FirstProblem(log.Replace("!!! the step failed", "the step went on", StringComparison.Ordinal)), "with no failure, the first warning");
		Equal(-1, ToolRunner.App.LogSearch.FirstProblem("21:00:01  all well\n21:00:02    a detail\n"), "a log with neither has none");
		var at = ToolRunner.App.LogSearch.Next(log, "STEP", 0);
		Equal(log.IndexOf("step", StringComparison.Ordinal), at, "case does not matter");
		Equal(at, ToolRunner.App.LogSearch.Next(log, "step", at + 1), "past the last match, from the top again");
		Equal(-1, ToolRunner.App.LogSearch.Next(log, "nowhere", 0), "not there");
		Equal(-1, ToolRunner.App.LogSearch.Next(log, "", 0), "nothing typed finds nothing");
	}

	// The command box keeps what holds every word typed, what starts with it first
	static void T_TheCommandBoxMatchesTheWordsTyped()
	{
		string[] items = ["Start", "Settings", "Settings of TexGen", "Run only TexGen", "Dry run"];
		Equal("Settings,Settings of TexGen", string.Join(",", ToolRunner.App.CommandBox.Match(items, x => x, "sett")), "the start of a word");
		Equal("Settings of TexGen,Run only TexGen", string.Join(",", ToolRunner.App.CommandBox.Match(items, x => x, "tex")), "inside a label, in the order given");
		Equal("Run only TexGen,Dry run", string.Join(",", ToolRunner.App.CommandBox.Match(items, x => x, "run")), "what starts with it first");
		Equal("Run only TexGen", string.Join(",", ToolRunner.App.CommandBox.Match(items, x => x, "tex run")), "every word must be there");
		Equal(items.Length, ToolRunner.App.CommandBox.Match(items, x => x, "  ").Count, "nothing typed: everything");
		Check(ToolRunner.App.MainViewModel.DoneAbout(new DateTime(2026, 1, 1, 21, 40, 0, DateTimeKind.Local)).StartsWith("done about ", StringComparison.Ordinal), "the finish time's words");
	}

	// A mod list that begins with a UTF-8 mark keeps it: a line written into it and taken out again leaves the file byte for
	// byte as it was - what the put-back of a run that only listed a mod must leave
	static void T_TheModListKeepsHowItIsWritten()
	{
		using var f = new FakeMo2();
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		var mark = Encoding.UTF8.GetPreamble();
		File.WriteAllBytes(list, [.. mark, .. File.ReadAllBytes(list)]);
		var before = File.ReadAllBytes(list);
		f.Mod("Loose", ("Loose.esp", false, []));
		var p = ProfileOf(f);
		Check(p.ListMod("Loose") && p.States().ContainsKey("Loose"), "(its line written)");
		Check(File.ReadAllBytes(list).AsSpan().StartsWith(mark), "the mark is kept when a line is written");
		p.SetMods(["Loose"], []);
		Check(File.ReadAllBytes(list).AsSpan().StartsWith(mark) && p.IsOn("Loose"), "and when a mod is switched");
		p.SetMods([], ["Loose"]);
		Check(p.UnlistMod("Loose") && !p.States().ContainsKey("Loose"), "(the line taken out again)");
		Check(File.ReadAllBytes(list).SequenceEqual(before), "the mod list is byte for byte as it was");
		Check(Profile.KeepAdded(before, new Dictionary<string, char>(), ["New One"]).AsSpan().StartsWith(mark), "a saved list put back with a line kept for a mod keeps its mark too");
	}

	// a finished run's old outputs are packed, its folder gone; undoing the run puts each output and the lists back,
	// sets the new outputs aside (never erased), takes out an output the run made new, keeps what the player changed; it can be done once,
	// only to the last run on that profile and those outputs; a switched-off pack keeps the folder
	static void T_TheLastRunCanBeUndone()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			ctx.Profile.EnsureMod("Brand New", ctx.Log);
			File.WriteAllText(Path.Combine(f.Mods, "Brand New", "fresh.txt"), "made by the run");
			ctx.Journal.Built.Add("Brand New");
			ctx.Journal.Added.Add("Brand New");
			ctx.Journal.Save();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject()));
		var before = f.Text("loadorder.txt");
		var oldBytes = File.ReadAllBytes(Path.Combine(f.Mods, "Output A", "OutA.esp"));
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(File.Exists(Path.Combine(j.BackupDir, RunUndo.OutputsZip)) && !Directory.Exists(Path.Combine(j.BackupDir, RunUndo.OutputsFolder)), "packed: the zip there, the folder gone");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")), "(the new output in its mod)");
		// the player changes something after the run
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		var r = Runner.Undo(Journal.Load(j.Folder));
		Equal(RunState.Restored, r.State, "undone: " + r.Verdict);
		Check(!File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")), "the new output left its mod");
		Check(File.ReadAllBytes(Path.Combine(f.Mods, "Output A", "OutA.esp")).SequenceEqual(oldBytes), "the old output is back, byte for byte");
		Check(File.Exists(Path.Combine(j.BackupDir, "undone", "Output A", "new.txt")), "the new output is set aside, never erased");
		Check(File.Exists(Path.Combine(j.BackupDir, "undone", "Brand New", "fresh.txt")), "so is one the run made new");
		var p = ProfileOf(f);
		Check(!p.States().ContainsKey("Brand New") && !Directory.Exists(Path.Combine(f.Mods, "Brand New")), "an output the run made new is out of the mod list and the mods folder");
		Check(!p.IsOn("Base Mod"), "what the player changed since is kept");
		Check(p.IsOn("Output A") && p.IsOn("Output B"), "the rest as before the run");
		Equal(before, f.Text("loadorder.txt"), "the load order as before");
		var again = Runner.Undo(Journal.Load(j.Folder));
		Check(again.Verdict.Contains("undone already", StringComparison.Ordinal), "only once: " + again.Verdict);
		Check(ToolRunner.App.Cli.Handles(["undo"]), "the command line takes it, and does not open the window instead");
		Check(LastRuns.Of(Journal.All(Runner.RunsDir(f.Data)), f.SetupFile).Count == 0, "an undone run no longer counts as a step's last run");
		// an older run cannot be undone past a newer one that changed the same profile or outputs; a later run that changed nothing is in nobody's way
		Journal J(string a_id, int a_days, string? a_setup = null, string a_profile = "P", string[]? a_built = null, RunState a_state = RunState.Finished) =>
			new() { Id = a_id, Folder = Path.Combine(Runner.RunsDir(f.Data), a_id), SetupFile = a_setup ?? f.SetupFile, Profile = a_profile, Started = DateTime.Now.AddDays(a_days), State = a_state, Built = [.. a_built ?? ["Output A"]] };
		var older = J("older", -1);
		bool Blocked(Journal a_later) => RunUndo.WhyNot(older, [older, a_later])?.Contains("last run", StringComparison.Ordinal) == true;
		Check(Blocked(J("newer", 0)), "only the last run");
		Check(!Blocked(J("empty", 0, a_built: [], a_state: RunState.Failed)), "a later run that built nothing is in nobody's way");
		Check(Blocked(J("cut off", 0, a_built: [], a_state: RunState.Running)), "a later run that did not finish is");
		Check(Blocked(J("other setup", 0, "other.json")), "so is another setup's run on the same profile");
		Check(Blocked(J("other profile", 0, "other.json", "Q")), "or on another profile with one of the same outputs");
		Check(!Blocked(J("elsewhere", 0, "other.json", "Q", ["Output Z"])), "another setup's run elsewhere is not");
		var sameFolder = J("same folder", 0, "other.json", "Q", ["Output Z"]);
		sameFolder.Parked.Add(new ParkRecord { Mod = "tool", From = @"D:\Tools\out", To = "x", Replaced = true, IsFolder = true, Moved = true });
		older.Parked.Add(new ParkRecord { Mod = "tool", From = "D:/Tools/out/", To = "y", Replaced = true, IsFolder = true, Moved = true });
		Check(Blocked(sameFolder), "one that moved the same tool folder is");
		sameFolder.Parked[0].Moved = false;
		Check(!Blocked(sameFolder), "not one whose move of it never ended");
		older.Parked.Clear();
		var typed = J("typed otherwise", 0, "other.json");
		typed.Mo2Exe = "D:/MO2/ModOrganizer.exe";
		older.Mo2Exe = @"D:\MO2\ModOrganizer.exe";
		older.Built = ["Output Y"];
		typed.Mo2Instance = "D:/MO2/Instance/";
		older.Mo2Instance = @"D:\MO2\Instance";
		Check(Blocked(typed), "a Mod Organizer typed another way is the same one");
		// a portable instance left unset in one setup, named in the other
		var named = J("named", 0, "other.json");
		named.Mo2Exe = @"D:\Portable\ModOrganizer.exe";
		named.Mo2Instance = @"D:\Portable";
		older.Mo2Exe = @"D:\Portable\ModOrganizer.exe";
		older.Mo2Instance = null;
		Check(Blocked(named), "a portable instance named in one setup and not the other is the same one");
		// a pack that is switched off keeps the folder
		var dir = Path.Combine(f.Root, "pack test");
		Directory.CreateDirectory(Path.Combine(dir, RunUndo.OutputsFolder, "Mod"));
		File.WriteAllText(Path.Combine(dir, RunUndo.OutputsFolder, "Mod", "a.txt"), "a");
		RunUndo.Pack(dir, RunLog.Null);
		Check(File.Exists(Path.Combine(dir, RunUndo.OutputsZip)), "packed");
		RunUndo.Unpack(dir);
		Equal("a", File.ReadAllText(Path.Combine(dir, RunUndo.OutputsFolder, "Mod", "a.txt")), "unpacked whole");
		Check(!File.Exists(Path.Combine(dir, RunUndo.OutputsZip)), "the zip gone after it");
		// a folder that will not go once the zip is whole: said as packed, and the zip brings everything back
		var held = Path.Combine(f.Root, "pack held");
		Directory.CreateDirectory(Path.Combine(held, RunUndo.OutputsFolder, "Mod"));
		File.WriteAllText(Path.Combine(held, RunUndo.OutputsFolder, "Mod", "a.txt"), "a");
		File.WriteAllText(Path.Combine(held, RunUndo.OutputsFolder, "Mod", "b.txt"), "b");
		using (var log = new RunLog(held)) {
			using (new FileStream(Path.Combine(held, RunUndo.OutputsFolder, "Mod", "a.txt"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
				RunUndo.Pack(held, log);
			}
		}
		Check(File.ReadAllText(Path.Combine(held, "log.txt")).Contains("are packed", StringComparison.Ordinal), "said as packed, not as left unpacked");
		RunUndo.Unpack(held);
		Check(File.ReadAllText(Path.Combine(held, RunUndo.OutputsFolder, "Mod", "b.txt")) == "b" && File.ReadAllText(Path.Combine(held, RunUndo.OutputsFolder, "Mod", "a.txt")) == "a", "and the zip brings both back");
	}

	// the backup an undo needs stays, however few runs are kept; a switched-off pack keeps the folder; a file a step changed in place goes back on its own, and the rest of that mod stays
	static void T_AnUndoFindsWhatItNeeds()
	{
		using var f = new FakeMo2();
		var built = true;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			if (built) {
				WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			}
		}));
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		setup.Root["keepBackups"] = 1;
		setup.Root["zipBackups"] = false;
		setup.Save(f.SetupFile);
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run that built");
		var first = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(Directory.Exists(Path.Combine(first.BackupDir, RunUndo.OutputsFolder)) && !File.Exists(Path.Combine(first.BackupDir, RunUndo.OutputsZip)), "a pack switched off: the folder stays");
		built = false;
		var noOutput = f.WriteSetup(("landscapefixer", new JsonObject()));
		noOutput.Root["keepBackups"] = 1;
		noOutput.Save(f.SetupFile);
		Thread.Sleep(1100);
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "a run that built nothing");
		Check(Directory.Exists(first.BackupDir), "the backup an undo needs is not pushed out by it");

		using var g = new FakeMo2();
		var inMod = Path.Combine(g.Mods, "Output A", "road.dds");
		File.WriteAllText(inMod, "plain");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.KeepFile(ctx, inMod);
			File.WriteAllText(inMod, "painted");
			StepKit.Settled(ctx, [inMod]);
			ctx.Journal.Built.Add("Output A");
			ctx.Journal.Save();
		}));
		g.WriteSetup(("landscapefixer", new JsonObject()));
		Equal(RunState.Finished, Runner.Run(new RunRequest(g.SetupFile, g.Data)).State, "a step that changed a file in place");
		var r = Runner.Undo(Journal.All(Runner.RunsDir(g.Data)).First());
		Equal(RunState.Restored, r.State, "undone: " + r.Verdict);
		Equal("plain", File.ReadAllText(inMod), "that file is back");
		Check(File.Exists(Path.Combine(g.Mods, "Output A", "OutA.esp")), "and the rest of the mod stays where it was");
	}

	// an undo cut off half way (a file held open) can be pressed again: what the first press put back stays, what it set aside stays
	static void T_AnUndoCutOffCanBePressedAgain()
	{
		using var f = new FakeMo2();
		File.WriteAllText(Path.Combine(f.Mods, "Output A", "old.txt"), "old A");
		File.WriteAllText(Path.Combine(f.Mods, "Output B", "old.txt"), "old B");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => WriteOutput(ctx, "synthesis", "Output B", "OutB.esp")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Output B" }));
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		var undone = Path.Combine(j.BackupDir, "undone");
		// an undo walks from the last step back: B goes first, then A's new output is held open
		using (new FileStream(Path.Combine(f.Mods, "Output A", "new.txt"), FileMode.Open, FileAccess.Read, FileShare.None)) {
			Equal(RunState.Failed, Runner.Undo(Journal.Load(j.Folder)).State, "the first press stops at the held file");
		}
		Equal("old B", File.ReadAllText(Path.Combine(f.Mods, "Output B", "old.txt")), "B is back after the first press");
		var cut = Journal.Load(j.Folder);
		Check(cut.Unfinished && cut.State == RunState.Failed && cut.Verdict.Contains("half way", StringComparison.Ordinal), "a half-undone run counts as not finished, and says so");
		Equal<string?>(null, RunUndo.WhyNot(cut, [cut]), "and it can always be ended");
		Check(Runner.Run(new RunRequest(f.SetupFile, f.Data)).State != RunState.Finished && Journal.All(Runner.RunsDir(f.Data)).Count() == 1, "so nothing new starts on it");
		// the window's put-back ends it
		var second = Runner.Restore(Journal.Load(j.Folder));
		Equal(RunState.Restored, second.State, "the second press: " + second.Verdict);
		Check(second.Verdict.StartsWith("undone", StringComparison.Ordinal) && !Journal.Load(j.Folder).Unfinished, "it is undone, and over");
		Equal("old B", File.ReadAllText(Path.Combine(f.Mods, "Output B", "old.txt")), "B's old output is not taken for the run's");
		Check(!File.Exists(Path.Combine(f.Mods, "Output B", "new.txt")) && File.Exists(Path.Combine(undone, "Output B", "new.txt")), "B's new output stays set aside");
		Equal("old A", File.ReadAllText(Path.Combine(f.Mods, "Output A", "old.txt")), "A is back after the second");
		Check(!File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")) && Directory.GetFiles(undone, "new.txt", SearchOption.AllDirectories).Length == 2, "and its new output set aside too");
	}

	// an undo cut off once the old mod list is back: the list no longer shows what the player changed, the run's record does
	static void T_AnUndoCutOffKeepsThePlayersChange()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		var plugins = Path.Combine(f.ProfileDir, "plugins.txt");
		File.WriteAllText(plugins, File.ReadAllText(plugins).Replace("*BaseMod.esp", "BaseMod.esp", StringComparison.Ordinal));
		// the mod list is written back before the plugin list, which is held open here
		using (new FileStream(plugins, FileMode.Open, FileAccess.Read, FileShare.Read)) {
			Equal(RunState.Failed, Runner.Undo(Journal.Load(j.Folder)).State, "the first press stops at the plugin list");
		}
		var second = Runner.Undo(Journal.Load(j.Folder));
		Equal(RunState.Restored, second.State, "the second press: " + second.Verdict);
		Check(!ProfileOf(f).IsOn("Base Mod"), "what the player changed is kept");
		// a backup that does not read has changed nothing, so nothing is held up
		using var g = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		g.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		Equal(RunState.Finished, Runner.Run(new RunRequest(g.SetupFile, g.Data)).State, "another run finished");
		var run = Journal.All(Runner.RunsDir(g.Data)).First();
		File.WriteAllText(Path.Combine(run.BackupDir, RunUndo.OutputsZip), "not a zip");
		Equal(RunState.Failed, Runner.Undo(Journal.Load(run.Folder)).State, "the zip does not read");
		Check(!Journal.Load(run.Folder).Unfinished, "and the profile is not held up by it");
	}

	// the instance folder a setup names is the one read, or none: with its ModOrganizer.ini gone, the instance beside the program is
	// never taken in its place
	static void T_TheInstanceASetupNamesIsNeverSwappedForAnother()
	{
		using var f = new FakeMo2();
		var moved = Path.Combine(f.Root, "Instance moved away");
		Directory.CreateDirectory(moved);
		var refused = "";
		try {
			Mo2Instance.Load(f.Exe, moved);
		} catch (FileNotFoundException e) {
			refused = e.Message;
		}
		Check(refused.Contains("instance folder", StringComparison.Ordinal), "an instance folder with no ModOrganizer.ini is refused, and said: " + refused);
		var beside = Path.Combine(f.Instance, "ModOrganizer.ini");
		Check(Files.SamePath(Mo2Instance.Load(f.Exe).IniPath, beside), "with no instance named, the one beside the program");
		Check(Files.SamePath(Mo2Instance.Load(f.Exe, "  ").IniPath, beside), "a blank names none");
	}

	// an undo works in the Mod Organizer the run used, also when the setup names another one by now: that other one's profile of
	// the same name is never written
	static void T_AnUndoWorksInTheModOrganizerTheRunUsed()
	{
		using var f = new FakeMo2();
		using var other = new FakeMo2();
		File.WriteAllText(Path.Combine(f.Mods, "Output A", "old.txt"), "old A");
		// (the two profiles' lists differ, so the run's lists written into the wrong one would show)
		var otherList = Path.Combine(other.ProfileDir, "modlist.txt");
		File.WriteAllText(otherList, "+Only In The Other\r\n" + File.ReadAllText(otherList));
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		string States(FakeMo2 a_m) => string.Join(", ", ProfileOf(a_m).States().OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value + p.Key));
		var statesBefore = States(f);
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(Files.SamePath(j.Mo2Exe, f.Exe), "the run's record names the Mod Organizer it worked in");
		var root = Setup.Load(f.SetupFile).Root;
		root["mo2"] = new JsonObject { ["exe"] = other.Exe, ["instance"] = other.Instance, ["profile"] = "Test" };
		File.WriteAllText(f.SetupFile, Setup.TextOf(root));
		var theirs = File.ReadAllBytes(Path.Combine(other.ProfileDir, "modlist.txt"));
		var r = Runner.Undo(Journal.Load(j.Folder));
		Equal(RunState.Restored, r.State, "undone: " + r.Verdict);
		Check(theirs.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(other.ProfileDir, "modlist.txt"))), "the other Mod Organizer's profile is as it was");
		Equal(statesBefore, States(f), "the run's own profile is as it was before the run");
		Equal("old A", File.ReadAllText(Path.Combine(f.Mods, "Output A", "old.txt")), "the old output is back where the run took it from");
	}

	// two steps writing one output each keep what they wrote; a tool folder's label never passes for a mod of that name; what a move that
	// failed left in a tool folder is never taken for the run's
	static void T_AnUndoKeepsEveryOutputAside()
	{
		using var f = new FakeMo2();
		var tool = Path.Combine(f.Root, "tool out");
		var other = Path.Combine(f.Root, "other tool out");
		var heldDir = Path.Combine(f.Root, "held tool out");
		var half = Path.Combine(f.Root, "half tool out");
		foreach (var d in new[] { tool, other, heldDir }) {
			Directory.CreateDirectory(d);
			File.WriteAllText(Path.Combine(d, "tool old.txt"), "old");
		}
		File.WriteAllText(Path.Combine(f.Mods, "Output A", "old.txt"), "old A");
		void Second(RunContext a_ctx, string a_text) => File.WriteAllText(Path.Combine(a_ctx.Mo2.ModDir("Output A"), "new.txt"), a_text);
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			Second(ctx, "first");
		}));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			WriteOutput(ctx, "synthesis", "Output A", "OutA.esp");
			Second(ctx, "second");
			StepKit.ParkDir(ctx, tool, "brand new");
			File.WriteAllText(Path.Combine(tool, "tool new.txt"), "made by the run");
			StepKit.ParkDir(ctx, other, "base mod");
			ctx.Profile.EnsureMod("Brand New", ctx.Log);
			File.WriteAllText(Path.Combine(f.Mods, "Brand New", "fresh.txt"), "made by the run");
			ctx.Journal.Built.Add("Brand New");
			ctx.Journal.Added.Add("Brand New");
			using (new FileStream(Path.Combine(heldDir, "tool old.txt"), FileMode.Open, FileAccess.Read, FileShare.None)) {
				try {
					StepKit.ParkDir(ctx, heldDir, "held");
					Check(false, "a held folder does not move");
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Check(!ctx.Journal.Parked.Any(p => p.Mod == "held"), "a move that moved nothing is not on record");
				}
			}
			// a move across drives that stopped half way: its record, and the two halves
			Directory.CreateDirectory(half);
			File.WriteAllText(Path.Combine(half, "rest.txt"), "rest");
			var moved = Path.Combine(ctx.BackupFolder, "outputs", "half");
			Directory.CreateDirectory(moved);
			File.WriteAllText(Path.Combine(moved, "moved.txt"), "moved");
			ctx.Journal.Parked.Add(new ParkRecord { Mod = "half", From = half, To = moved, Replaced = true, IsFolder = true });
			ctx.Journal.Save();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject()));
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		var r = Runner.Undo(Journal.Load(j.Folder));
		Equal(RunState.Restored, r.State, "undone: " + r.Verdict);
		var undone = Path.Combine(j.BackupDir, "undone");
		var kept = Directory.GetFiles(undone, "new.txt", SearchOption.AllDirectories).Select(File.ReadAllText).Order(StringComparer.Ordinal).ToList();
		Equal("first|second", string.Join("|", kept), "both steps' files are kept");
		Equal("old A", File.ReadAllText(Path.Combine(f.Mods, "Output A", "old.txt")), "the old output is back");
		Equal(1, Directory.GetFiles(undone, "fresh.txt", SearchOption.AllDirectories).Length, "a mod with a tool folder's name is set aside all the same");
		Check(!ProfileOf(f).States().ContainsKey("Brand New"), "and taken out");
		Check(!ProfileOf(f).IsOn("Base Mod"), "the player's change to a mod named like a tool folder's label is kept");
		Check(File.Exists(Path.Combine(tool, "tool old.txt")) && !File.Exists(Path.Combine(tool, "tool new.txt")), "the tool's folder is as it was");
		Equal(1, Directory.GetFiles(undone, "tool new.txt", SearchOption.AllDirectories).Length, "what the run left in it is set aside");
		Check(File.Exists(Path.Combine(heldDir, "tool old.txt")) && Directory.GetFiles(undone, "tool old.txt", SearchOption.AllDirectories).Length == 0, "what a failed move touched stays where it is");
		Check(File.Exists(Path.Combine(half, "rest.txt")) && File.Exists(Path.Combine(half, "moved.txt")), "the halves of a move that stopped come together again");
	}

	// two steps that write the same file in place, and a file written in place before a later step replaces its whole mod: an
	// undo ends on what stood there before the run, for a file that was there and for one that was not
	static void T_AnUndoEndsOnWhatStoodThereBeforeTheRun()
	{
		using var f = new FakeMo2();
		var modA = Path.Combine(f.Mods, "Output A");
		var modB = Path.Combine(f.Mods, "Output B");
		File.WriteAllText(Path.Combine(modA, "was there.esp"), "the player's A");
		File.WriteAllText(Path.Combine(modA, "untouched.txt"), "stays");
		File.WriteAllText(Path.Combine(modB, "was there.esp"), "the player's B");
		static void InPlace(RunContext a_ctx, string a_mod, string a_file, string a_text)
		{
			var path = Path.Combine(a_ctx.Mo2.ModDir(a_mod), a_file);
			StepKit.KeepInPlace(a_ctx, a_mod, a_file);
			File.WriteAllText(path, a_text);
			StepKit.Settled(a_ctx, a_mod, [path]);
		}
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			InPlace(ctx, "Output A", "was there.esp", "the first step's");
			InPlace(ctx, "Output A", "not there before.esp", "the first step's");
			InPlace(ctx, "Output B", "was there.esp", "the first step's");
			InPlace(ctx, "Output B", "not there before.esp", "the first step's");
		}));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			InPlace(ctx, "Output A", "was there.esp", "the second step's");
			InPlace(ctx, "Output A", "not there before.esp", "the second step's");
			// the whole of Output B replaced: what goes into the backup holds the first step's two files
			WriteOutput(ctx, "synthesis", "Output B", "OutB.esp");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject()));
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		Equal("the second step's", File.ReadAllText(Path.Combine(modA, "was there.esp")), "after the run the file is the last step's");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		var r = Runner.Undo(Journal.Load(j.Folder));
		Equal(RunState.Restored, r.State, "undone: " + r.Verdict);
		Equal("the player's A", File.ReadAllText(Path.Combine(modA, "was there.esp")), "written in place twice: back to what the player had, not the first step's");
		Check(!File.Exists(Path.Combine(modA, "not there before.esp")), "a file two steps made new is gone");
		Equal("stays", File.ReadAllText(Path.Combine(modA, "untouched.txt")), "the rest of that mod is as it was");
		Equal("the player's B", File.ReadAllText(Path.Combine(modB, "was there.esp")), "written in place, then its mod replaced whole: back to what the player had");
		Check(!File.Exists(Path.Combine(modB, "not there before.esp")), "and the file the first step made new there is gone too");
	}

	// the other order: a step replaces a whole mod, and a later step then writes files in place into the replacement. The
	// file records of that later step say what the REPLACEMENT held, not what stood there before the run: the old mod comes
	// back whole from the backup and those records are left out
	static void T_AnUndoAfterAModWasReplacedAndThenWrittenInto()
	{
		using var f = new FakeMo2();
		var modA = Path.Combine(f.Mods, "Output A");
		File.WriteAllText(Path.Combine(modA, "was there.esp"), "the player's");
		static void InPlace(RunContext a_ctx, string a_mod, string a_file, string a_text)
		{
			var path = Path.Combine(a_ctx.Mo2.ModDir(a_mod), a_file);
			StepKit.KeepInPlace(a_ctx, a_mod, a_file);
			File.WriteAllText(path, a_text);
			StepKit.Settled(a_ctx, a_mod, [path]);
		}
		// the replacement holds OutA.esp and new.txt, and no "was there.esp"
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			InPlace(ctx, "Output A", "was there.esp", "the second step's");
			InPlace(ctx, "Output A", "not there before.esp", "the second step's");
			InPlace(ctx, "Output A", "new.txt", "the second step's");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject()));
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		Equal("the second step's", File.ReadAllText(Path.Combine(modA, "was there.esp")), "after the run the file is the second step's");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(j.Parked.Any(p => p.Seq > 0) && j.Kept.All(k => k.Seq > j.Parked.Max(p => p.Seq)), "the record keeps the order: the mod was replaced before the files were written");
		var r = Runner.Undo(Journal.Load(j.Folder));
		Equal(RunState.Restored, r.State, "undone: " + r.Verdict);
		Equal("the player's", File.ReadAllText(Path.Combine(modA, "was there.esp")), "a file the player had and the replacement did not: back as the player had it, not taken out");
		Check(!File.Exists(Path.Combine(modA, "not there before.esp")), "a file neither had is gone");
		Check(!File.Exists(Path.Combine(modA, "new.txt")), "a file only the replacement had is not laid into the old mod");
		// records from before the order was kept (no numbers) are read as they always were
		var old = new List<KeptFile> { new() { Path = @"X:\mods\M\a.esp", Backup = null, Settled = true } };
		Equal(1, RunUndo.FirstStates(old, [new ParkRecord { Mod = "M", From = @"X:\mods\M", To = "y", Replaced = true }]).Count, "an older run's records still go back");
		Equal(0, RunUndo.FirstStates([new KeptFile { Path = @"X:\mods\M\a.esp", Settled = true, Seq = 2 }], [new ParkRecord { Mod = "M", From = @"X:\mods\M", To = "y", Replaced = true, Seq = 1 }]).Count, "kept after its mod was replaced: left out");
		Equal(1, RunUndo.FirstStates([new KeptFile { Path = @"X:\mods\M\a.esp", Settled = true, Seq = 1 }], [new ParkRecord { Mod = "M", From = @"X:\mods\M", To = "y", Replaced = true, Seq = 2 }]).Count, "kept before: its first record stands");
		Equal(1, RunUndo.FirstStates([new KeptFile { Path = @"X:\mods\Other\a.esp", Settled = true, Seq = 2 }], [new ParkRecord { Mod = "M", From = @"X:\mods\M", To = "y", Replaced = true, Seq = 1 }]).Count, "another mod's park says nothing about it");
	}

	// a made-up hold on a process (StartedSet): it says when it was given up
	sealed class Hold : IDisposable
	{
		public bool GivenUp { get; private set; }

		public void Dispose() => GivenUp = true;
	}

	// Two looks at once - the watch's beat and a Stop's. A look reads which numbers are held
	// open as it begins and counts on that to its end. A second look on another thread that found the parent ended gave its
	// hold up under the first: from then on the number could change hands while the first still read every child under it as
	// the run's own. Looks take turns: the second begins only when the first is done, and no hold is given up under a look.
	static void T_ALookKeepsWhatItHoldsUntilItIsDone()
	{
		var t = new DateTime(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);
		var world = new System.Collections.Concurrent.ConcurrentDictionary<int, DateTime?>();
		world[10] = t;
		var holds = new System.Collections.Concurrent.ConcurrentBag<Hold>();
		var set = new StartedSet(p => world.GetValueOrDefault(p), (_, _) => {
			var hold = new Hold();
			holds.Add(hold);
			return hold;
		});
		set.Add(10);
		// the second look has come to the point where it waits for its turn / has begun its own work (its list was asked for)
		using var secondAtItsTurn = new ManualResetEventSlim();
		using var secondBegan = new ManualResetEventSlim();
		var cameToItsTurn = false;
		var beganUnderTheFirst = false;
		var givenUpUnderTheFirst = false;
		Task? second = null;
		var lists = 0;
		IReadOnlyCollection<(int Pid, int Parent)>? FirstLooksList()
		{
			if (++lists == 1) {
				// The first look has read "10 is held". Now the parent ends and a second look starts on another thread: let in,
				// it would find the parent ended, look for its children one last time and give its hold up - under the first.
				// That the second one has COME to its turn is told by the set itself (a look that had merely not started yet
				// would prove nothing); from there, only the wait for its turn can keep it from asking for its list
				world.TryRemove(10, out _);
				set.AboutToWaitForItsTurn = secondAtItsTurn.Set;
				second = Task.Run(() => set.Sweep(() => {
					secondBegan.Set();
					return [];
				}, () => t.AddSeconds(20)));
				cameToItsTurn = secondAtItsTurn.Wait(TimeSpan.FromSeconds(10));
				beganUnderTheFirst = secondBegan.Wait(TimeSpan.FromMilliseconds(300));
				givenUpUnderTheFirst = holds.Any(h => h.GivenUp);
			}
			return [];
		}
		set.Sweep(FirstLooksList, () => t.AddSeconds(10));
		set.AboutToWaitForItsTurn = null;
		Check(cameToItsTurn, "(the second look had come to its turn while the first was out)");
		Check(!beganUnderTheFirst, "a second look that has come to its turn does not begin while the first is out");
		Check(!givenUpUnderTheFirst, "so no hold the first look counts on is given up under it");
		Check(second != null && second.Wait(TimeSpan.FromSeconds(10)), "the second look runs once the first is done");
		Check(holds.Count == 1 && holds.All(h => h.GivenUp) && set.Held == 0, "the ended parent's hold is given up in the end, by the look that retired it");
		// ... and the run's end waits for a look that is out before it gives every hold up
		var late = new StartedSet(p => world.GetValueOrDefault(p), (_, _) => new Hold());
		world[30] = t;
		late.Add(30);
		using var lookIsOut = new ManualResetEventSlim();
		using var letItEnd = new ManualResetEventSlim();
		using var releaseAtItsTurn = new ManualResetEventSlim();
		var look = Task.Run(() => late.Sweep(() => {
			lookIsOut.Set();
			letItEnd.Wait(TimeSpan.FromSeconds(10));
			return [];
		}, () => t.AddSeconds(10)));
		Check(lookIsOut.Wait(TimeSpan.FromSeconds(10)), "(a look is out)");
		late.AboutToWaitForItsTurn = releaseAtItsTurn.Set;
		var release = Task.Run(late.Release);
		Check(releaseAtItsTurn.Wait(TimeSpan.FromSeconds(10)), "(the giving up of the holds has come to its turn while that look is out)");
		Check(!release.Wait(TimeSpan.FromMilliseconds(300)) && late.Held == 1, "the holds are not given up while that look is out");
		letItEnd.Set();
		Check(look.Wait(TimeSpan.FromSeconds(10)) && release.Wait(TimeSpan.FromSeconds(10)) && late.Held == 0, "they are once it is done");
		// ... and once they are given up nothing takes a new one: a look that comes late (the watch's last beat, when its
		// thread was slow to end) or a process put on record after it - nothing would ever give such a hold up.
		// What is on record can still be asked
		var taken = new System.Collections.Concurrent.ConcurrentBag<Hold>();
		var over = new StartedSet(p => world.GetValueOrDefault(p), (_, _) => {
			var hold = new Hold();
			taken.Add(hold);
			return hold;
		});
		world[50] = t;
		over.Add(50);
		over.Release();
		Check(taken.Count == 1 && taken.All(h => h.GivenUp) && over.Released, "(every hold given up)");
		world[51] = t.AddSeconds(1);
		over.Sweep(() => [(51, 50)], () => t.AddSeconds(10));
		world[52] = t.AddSeconds(2);
		over.Add(52);
		Check(taken.Count == 1 && over.Held == 0, "after that, neither a late look nor a process put on record takes a hold");
		Check(over.Contains(50) && over.Contains(52) && over.Owned().Contains(51) && over.LiveAt().ContainsKey(52), "and what is on record can still be asked");
	}

	// what the run's processes start themselves stays the run's own after the one that started it has ended
	static void T_AChildOfWhatTheRunStartedIsTheRunsToo()
	{
		var t = new DateTime(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);
		var starts = new Dictionary<int, DateTime?> { [10] = t, [11] = t.AddSeconds(5), [12] = t.AddSeconds(6), [13] = t.AddSeconds(-60), [14] = t.AddSeconds(7) };
		DateTime? StartOf(int a_pid) => starts.GetValueOrDefault(a_pid);
		var launcher = new Dictionary<int, DateTime> { [10] = t };
		// a parent that is held open: its number is certainly its own for good
		static DateTime Held(int a_pid) => DateTime.MaxValue;
		// 11 the launcher's child, 12 that one's child, 13 older than the launcher under the same parent number, 14 somebody else's
		var listed = t.AddMinutes(5);
		var kids = StartedSet.Children(launcher, [(10, 1), (11, 10), (12, 11), (13, 10), (14, 99)], StartOf, listed, Held);
		Equal("11,12", string.Join(",", kids.Keys.Order()), "a child and its child are the run's; one older than its \"parent\" and a stranger are not");
		// the launcher has ended and nothing has its number
		starts.Remove(10);
		Equal("11,12", string.Join(",", StartedSet.Children(launcher, [(11, 10), (12, 11)], StartOf, listed, Held).Keys.Order()), "found all the same once the launcher has ended");
		// The launcher was NOT held open: its number may have been another program's since it was last seen alive, and a holder
		// that has ended again leaves nothing to tell it by. Only a child that had started by then is certainly its own
		Equal("11,12", string.Join(",", StartedSet.Children(launcher, [(11, 10), (12, 11)], StartOf, listed, _ => t.AddSeconds(5)).Keys.Order()),
			"a child that had started while the launcher was last seen alive is its own, and that child's child with it");
		Equal("", string.Join(",", StartedSet.Children(launcher, [(11, 10), (12, 11)], StartOf, listed, _ => t.AddSeconds(4)).Keys.Order()),
			"one that started after that is taken for nobody's: the number may have changed hands in between");
		// the launcher's number is another program's by now: what that one started is never the run's
		starts[10] = t.AddSeconds(30);
		starts[15] = t.AddSeconds(40);
		Equal("11", string.Join(",", StartedSet.Children(launcher, [(10, 1), (11, 10), (15, 10)], StartOf, listed, Held).Keys.Order()), "a child started after the number changed hands is the new owner's");
		// a child's own number changed hands after the list was taken: the row named the child that has ended, not the newcomer
		starts.Remove(10);
		starts[11] = listed.AddSeconds(2);
		Equal("", string.Join(",", StartedSet.Children(launcher, [(11, 10)], StartOf, listed, Held).Keys.Order()), "a process that started after the list was taken is nobody's child by that list");
		starts[11] = listed;
		Equal("", string.Join(",", StartedSet.Children(launcher, [(11, 10)], StartOf, listed, Held).Keys.Order()), "nor one that started at the very reading of the clock: only before it is certain");

		// A number that changes hands between the list and the clock. The look reads its clock BEFORE each list, so a
		// newcomer that took an ended child's number right after the list never passes for that child - at the first list
		// and at the second one (made when a parent has ended) alike.
		var world = new Dictionary<int, DateTime?> { [10] = t, [13] = t.AddSeconds(1) };
		// what is on record is held open (in these made-up worlds: a marker that counts when it is given up)
		var holds = new List<Hold>();
		Hold Keep(int a_pid, DateTime a_at)
		{
			holds.Add(new Hold());
			return holds[^1];
		}
		var set = new StartedSet(p => world.GetValueOrDefault(p), Keep);
		set.Add(10);
		var tick = 0;
		DateTime Clock() => t.AddSeconds(10 + ++tick);
		var lists = 0;
		IReadOnlyCollection<(int Pid, int Parent)>? List()
		{
			if (++lists == 1) {
				// the first list names child 11 and child 13; right after it 11 ends and a stranger gets its number - and the
				// launcher ends too, so a second list is made
				world[11] = Clock();
				world.Remove(10);
				return [(10, 1), (11, 10), (13, 10)];
			}
			// the second list names child 12 (and the stranger under 11 with its own parent); right after it 12 ends and
			// another stranger gets its number
			world[12] = Clock();
			return [(11, 99), (12, 10), (13, 10)];
		}
		set.Sweep(List, Clock);
		Equal(2, lists, "a parent that has ended is looked for once more, in a list read fresh");
		Equal("13", string.Join(",", set.Owned().Order()), "a number that changed hands right after a list was taken is nobody's child; the child that had started before the clock was read is the run's");
		// Windows would not give its list: nothing is learned from that look, and nothing forgotten
		set.Sweep(() => null, Clock);
		Equal("13", string.Join(",", set.Owned().Order()), "a look Windows would not answer changes nothing");
		// The clock has ticks: both readings of one look can fall in the tick a child started in. That child is not taken
		// at that look - and its ended parent is NOT retired over it, so the next look, with a later reading, finds it.
		var tickWorld = new Dictionary<int, DateTime?> { [20] = t, [21] = t.AddSeconds(30) };
		var tickSet = new StartedSet(p => tickWorld.GetValueOrDefault(p), Keep);
		tickSet.Add(20);
		tickWorld.Remove(20);
		var sameTick = t.AddSeconds(30);
		tickSet.Sweep(() => [(21, 20)], () => sameTick);
		Equal("", string.Join(",", tickSet.Owned().Order()), "a child that started in the very tick of the reading is not taken yet");
		Equal(1, tickSet.Held, "and its ended parent is still held open for it");
		tickSet.Sweep(() => [(21, 20)], () => sameTick.AddSeconds(3));
		Equal("21", string.Join(",", tickSet.Owned().Order()), "its parent was kept for it: the next look, a tick later, puts it on record");
		Equal(1, tickSet.Held, "the parent's hold is given up once its children were looked for the last time; the child is held in its place");
		tickWorld.Remove(21);
		tickSet.Sweep(() => [], () => sameTick.AddSeconds(6));
		Equal(0, tickSet.Held, "and the child's when it has ended too: nothing is held for a record that is done");
		Check(holds.Count > 0 && holds.Count(h => !h.GivenUp) == set.Held, "every hold that is no longer on record was given up");
		set.Release();
		Check(set.Held == 0 && holds.All(h => h.GivenUp), "and the rest when the run is over");

		// The number that changes hands twice between two looks: parent 30 starts child 31 in
		// the very tick of a look and ends, so it is kept for the next look; before that look a stranger gets the number 30,
		// starts 32 and ends. Nothing is left of the stranger - only 32, with "30" as its parent.
		void TwiceBetweenLooks(StartedSet a_set, Dictionary<int, DateTime?> a_world)
		{
			a_set.Add(30);
			a_set.Sweep(() => [(30, 1)], () => t.AddSeconds(28));
			a_world[31] = t.AddSeconds(30);
			a_world.Remove(30);
			a_set.Sweep(() => [(31, 30)], () => t.AddSeconds(30));
			a_world[32] = t.AddSeconds(32);
			a_set.Sweep(() => [(31, 30), (32, 30)], () => t.AddSeconds(35));
		}
		// not held open (its hold could not be taken): a child that started after it was last seen alive is nobody's - the
		// stranger's 32 is never the run's, and 31 is given up with it rather than guessed at
		var loose = new Dictionary<int, DateTime?> { [30] = t };
		var looseSet = new StartedSet(p => loose.GetValueOrDefault(p), (_, _) => null);
		TwiceBetweenLooks(looseSet, loose);
		Equal("", string.Join(",", looseSet.Owned().Order()), "a number that may have changed hands since its process was last seen alive brings no child onto the record");
		// seen alive at a look whose own tick its child started in: that child was not taken then, and is certainly its own
		// at the next look, held or not (it had started by the time its parent was last seen alive)
		var late = new Dictionary<int, DateTime?> { [40] = t, [41] = t.AddSeconds(10) };
		var lateSet = new StartedSet(p => late.GetValueOrDefault(p), (_, _) => null);
		lateSet.Add(40);
		lateSet.Sweep(() => [(40, 1), (41, 40)], () => t.AddSeconds(10));
		Equal("40", string.Join(",", lateSet.Owned().Order()), "(at the look whose tick the child started in, only the parent itself is on record)");
		late.Remove(40);
		lateSet.Sweep(() => [(41, 40)], () => t.AddSeconds(13));
		Equal("41", string.Join(",", lateSet.Owned().Order()), "a child that had started by the time its parent was last seen alive is the run's, hold or no hold");
		// held open: Windows gives the number 30 to nobody else, so the stranger cannot have had it - and the child that
		// started in the tick of the look is found at the next one, as before
		var tight = new Dictionary<int, DateTime?> { [30] = t };
		var tightSet = new StartedSet(p => tight.GetValueOrDefault(p), Keep);
		tightSet.Add(30);
		tightSet.Sweep(() => [(30, 1)], () => t.AddSeconds(28));
		tight[31] = t.AddSeconds(30);
		tight.Remove(30);
		tightSet.Sweep(() => [(31, 30)], () => t.AddSeconds(30));
		tightSet.Sweep(() => [(31, 30)], () => t.AddSeconds(35));
		Equal("31", string.Join(",", tightSet.Owned().Order()), "held open, the ended parent's late child is still found");
		// for real: a process held open keeps its number after it has ended, and reads as gone all the same
		using (var held = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!) {
			using var endHeld = new StandIn(held);
			var heldAt = Procs.StartTime(held.Id) ?? throw new InvalidOperationException("the stand-in's start time could not be read");
			Check(Procs.Pin(held.Id, heldAt.AddSeconds(-90)) == null, "a process that started at another time is not the one to hold");
			using var pin = Procs.Pin(held.Id, heldAt);
			Check(pin != null, "the very process is held open");
			held.Kill();
			Check(held.WaitForExit(10000), "the held process has ended");
			Check(Procs.StartTime(held.Id) == null && !Procs.Alive(held.Id) && Procs.Image(held.Id) == "", "ended and still held, it reads as gone - never as a program that still runs");
			Check(!Procs.Snapshot().Any(r => r.Pid == held.Id), "and Windows lists nobody under its number while it is held");
		}
		Check(Procs.StartClock() <= DateTime.UtcNow.AddSeconds(1) && Procs.StartClock() > DateTime.UtcNow.AddMinutes(-1), "the clock process starts are stamped with is the PC's own");
		using (var self = Process.GetCurrentProcess()) {
			Check(self.StartTime.ToUniversalTime() < Procs.StartClock(), "and a process that runs started before a reading of it");
		}

		// for real: a launcher starts its program and ends (ping stands in for the program)
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", []));
		var ctx = StepContext(f, "landscapefixer");
		using var log = ctx.Log;
		using var start = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c start \"\" /b ping -n 40 127.0.0.1 >nul & ping -n 3 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
		ctx.Control.Started.Add(start.Id);
		var mine = new List<int>();
		try {
			Check(ctx.Control.Started.StartedImage("CMD.EXE") && !ctx.Control.Started.StartedImage("ping.exe"),
				"the names of what the run started itself are kept: a child under another name is something a tool opened, which a Stop never ends");
			Check(start.WaitForExit(30000), "the launcher has ended");
			Equal(0, ctx.Control.Started.Live().Count, "nothing the run started itself is left");
			mine = StepKit.StartedRunning(ctx.Control, "PING.EXE");
			Equal(1, mine.Count, "the program it started is still the run's own, found at the first look after the launcher ended");
			Check(ctx.Control.Started.Owned().Contains(mine.FirstOrDefault()), "and on record as that");
		} finally {
			// whatever became of the checks: nothing this test started is left running
			ctx.Control.Started.Sweep();
			ctx.Control.Started.Owned().ForEach(Procs.KillTree);
		}
		ctx.Control.Started.Sweep();
		Check(!ctx.Control.Started.Owned().Contains(mine.FirstOrDefault()), "once it has ended it is no longer on record");

		// a number that changed hands: a program is ended only as the very process on record - its number AND its start time
		using var bystander = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
		using var endBystander = new StandIn(bystander);
		var began = Procs.StartTime(bystander.Id) ?? throw new InvalidOperationException("the stand-in's start time could not be read");
		Check(!Procs.KillTree(bystander.Id, began.AddSeconds(-90)) && !bystander.HasExited, "a process that has the number of one on record but started at another time is never ended as that one");
		Check(Procs.KillTree(bystander.Id, began) && bystander.WaitForExit(5000), "the very process on record is");

		// ... and ended ALONE: what it started keeps running (the game ended over a box it cannot have answered takes nothing
		// under it with it that the run has no record of)
		using var parent = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c \"ping -n 30 127.0.0.1 >nul\"") { UseShellExecute = false, CreateNoWindow = true })!;
		using var endParent = new StandIn(parent);
		var parentBegan = Procs.StartTime(parent.Id) ?? throw new InvalidOperationException("the stand-in's start time could not be read");
		var child = 0;
		for (var i = 0; i < 100 && child == 0; ++i) {
			child = Procs.Tree(parent.Id).FirstOrDefault(p => p != parent.Id && Procs.Image(p).Equals("PING.EXE", StringComparison.OrdinalIgnoreCase));
			if (child == 0) {
				Thread.Sleep(50);
			}
		}
		Check(child != 0, "(the stand-in started its child)");
		var childBegan = Procs.StartTime(child) ?? throw new InvalidOperationException("the child's start time could not be read");
		try {
			Check(!Procs.KillOnly(parent.Id, parentBegan.AddSeconds(-90)) && !parent.HasExited, "ended alone: never under a number that is another process's by now");
			Check(Procs.KillOnly(parent.Id, parentBegan) && parent.WaitForExit(5000), "the very process on record is ended");
			Check(Procs.StartTime(child) == childBegan, "and what it started is still running");
		} finally {
			Procs.KillTree(child, childBegan);
		}
	}

	// the leak guard's tree: every process held from the moment it is listed, each with the start time read through its hold;
	// a process that ended while held keeps its number (nobody else can take it), and the guard's end by number and start time
	// ends that tree only
	static void T_AHeldTreeKeepsEachProcessItsOwn()
	{
		using var parent = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c \"ping -n 30 127.0.0.1 >nul\"") { UseShellExecute = false, CreateNoWindow = true })!;
		using var endParent = new StandIn(parent);
		List<(int Pid, DateTime Started, IDisposable Hold)> tree = [];
		bool HasPing() => tree.Any(t => Procs.Image(t.Pid).Equals("PING.EXE", StringComparison.OrdinalIgnoreCase));
		for (var i = 0; i < 100 && !HasPing(); ++i) {
			foreach (var (_, _, hold) in tree) {
				hold.Dispose();
			}
			tree = Procs.HeldTree(parent.Id);
			if (!HasPing()) {
				Thread.Sleep(50);
			}
		}
		try {
			Check(HasPing(), "the stand-in and the program it started are both held");
			Check(tree[0].Pid == parent.Id && tree[0].Started == parent.StartTime.ToUniversalTime(), "the root first, with its own start time");
			var child = tree.First(t => Procs.Image(t.Pid).Equals("PING.EXE", StringComparison.OrdinalIgnoreCase));
			Check(child.Started == Procs.StartTime(child.Pid), "its child, with the start time Windows gives it");
			Check(tree.Skip(1).All(t => t.Started >= tree[0].Started), "every child started after its parent");
			foreach (var (pid, at, _) in tree.AsEnumerable().Reverse()) {
				Procs.KillTree(pid, at);
			}
			Check(parent.WaitForExit(5000), "ended through its number and start time, children first");
			Check(Procs.StartTime(child.Pid) == null, "the child too - and, held, its number is still nobody else's");
		} finally {
			foreach (var (_, _, hold) in tree) {
				hold.Dispose();
			}
		}
		Equal(0, Procs.HeldTree(parent.Id).Count, "a tree whose root has ended holds nothing");
	}

	// what a run's backup holds is never written over: not by a put-back that is tried again, not by a second failed step
	static void T_NothingInARunsBackupIsWrittenOver()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", []));
		var ctx = StepContext(f, "landscapefixer");
		using var log = ctx.Log;
		var mod = Path.Combine(f.Mods, "Output A");
		// a step parked the mod and half wrote its new output; an earlier try of this put-back kept a file of the same name
		StepKit.ParkMod(ctx, "landscapefixer", "Output A");
		File.WriteAllText(Path.Combine(mod, "half.esp"), "this try's");
		var kept = Path.Combine(ctx.BackupFolder, "unfinished", "Output A");
		Directory.CreateDirectory(kept);
		File.WriteAllText(Path.Combine(kept, "half.esp"), "kept by an earlier try");
		Equal(0, Runner.PutBackParked(ctx, "landscapefixer").Count, "the put-back");
		Equal("kept by an earlier try", File.ReadAllText(Path.Combine(kept, "half.esp")), "what an earlier try kept in the backup is not written over");
		Equal("this try's", File.ReadAllText(Path.Combine(kept + " 2", "half.esp")), "this try's goes into a folder of its own");
		Check(File.Exists(Path.Combine(mod, "OutA.esp")), "and the old output is back in its mod");
		var free = Path.Combine(f.Root, "free");
		Directory.CreateDirectory(free);
		var name = Path.Combine(free, "tool.log");
		Equal(name, Files.FreeFile(name), "a file name that is free is kept");
		File.WriteAllText(name, "1");
		Equal(Path.Combine(free, "tool (2).log"), Files.FreeFile(name), "one that is taken gets a number");
		File.WriteAllText(Path.Combine(free, "tool (2).log"), "2");
		Equal(Path.Combine(free, "tool (3).log"), Files.FreeFile(name), "and the next one the next");

		// three files of one name kept, the first taken out of the record again: each copy stays its own file's
		var kept1 = Path.Combine(f.Root, "one", "settings.ini");
		var kept2 = Path.Combine(f.Root, "two", "settings.ini");
		var kept3 = Path.Combine(f.Root, "three", "settings.ini");
		foreach (var (path, text) in new[] { (kept1, "one"), (kept2, "two"), (kept3, "three") }) {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, text);
		}
		StepKit.KeepFile(ctx, kept1);
		StepKit.KeepFile(ctx, kept2);
		StepKit.Forget(ctx, kept1);
		StepKit.KeepFile(ctx, kept3);
		var copies = ctx.Journal.Kept.Where(k => k.Path == kept2 || k.Path == kept3).Select(k => k.Backup!).ToList();
		Check(copies.Count == 2 && copies.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "two kept files of one name have a copy each, also after a record was taken out: " + string.Join(" | ", copies));
		File.WriteAllText(kept2, "changed");
		File.WriteAllText(kept3, "changed");
		Equal(0, StepKit.PutFilesBack(ctx).Count, "both go back");
		Check(File.ReadAllText(kept2) == "two" && File.ReadAllText(kept3) == "three", "each to its own old bytes");

		// a finished part of a step: built, and its old output kept out, in ONE write of the record
		StepKit.ParkMod(ctx, "landscapefixer", "Output B");
		ctx.Journal.Built.Clear();
		ctx.Journal.Save();
		ctx.Finished("Output B");
		var onDisk = Journal.Load(ctx.Journal.Folder);
		Check(onDisk.Built.Contains("Output B") && onDisk.Parked.Any(p => p.Mod == "Output B" && p.Replaced), "on record as built and as replaced together");

		// an add-on's program held from the look at its bytes until it runs: read and hashed under the hold, never replaced
		var program = Path.Combine(f.Root, "addon tool.exe");
		File.WriteAllText(program, "the program the player allowed");
		using (AddOns.Hold(program)) {
			Equal("the program the player allowed", File.ReadAllText(program), "it can still be read while it is held");
			Throws<IOException>(() => File.WriteAllText(program, "another program"), "nothing writes over it while it is held");
			Throws<IOException>(() => File.Move(program, program + ".old"), "nor moves it away");
		}
		File.WriteAllText(program, "an update");
		Equal("an update", File.ReadAllText(program), "and it is free again afterwards");
		Throws<StepFailedException>(() => AddOns.Hold(Path.Combine(f.Root, "no such tool.exe")), "a program that is not there says so");

		// Mod Organizer is never closed under Faster Archives' own program: it would end a repack half way
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var busyWait = Mo2Control.BusyWait;
		Mo2Control.BusyWait = TimeSpan.FromSeconds(1);
		try {
			var c = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => true, ForeignProbe = () => [], RepackProbe = () => [4242] };
			try {
				c.Close();
				Check(false, "Mod Organizer was closed under a repack");
			} catch (Mo2BusyException e) {
				Check(e.Message.Contains("Faster Archives", StringComparison.Ordinal), "the reason is named: " + e.Message);
			}
			// ... nor when Windows would not list the running programs: that is no answer, least of all "no repack runs"
			var blind = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => true, ForeignProbe = () => [], RepackProbe = () => null };
			try {
				blind.Close();
				Check(false, "Mod Organizer was closed on a process list Windows would not give");
			} catch (Mo2BusyException e) {
				Check(e.Message.Contains("would not list the running programs", StringComparison.Ordinal), "the reason is named: " + e.Message);
			}
			var real = new Mo2Control(mo2, "Test", RunLog.Null);
			Check(real.Repackers() != null, "the look for a repack, read off Windows, is an answer");
			Procs.FailLooks = 1;
			Check(real.Repackers() == null, "a look that failed is told apart from \"none runs\"");
			Check(real.Repackers() != null, "and read again once Windows answers");
			Procs.FailLooks = 1;
			_ = real.ForeignChildren(false);
			Check(real.ForeignBlind, "the look for the player's own programs under Mod Organizer says when Windows would not answer it");
			_ = real.ForeignChildren(false);
			Check(!real.ForeignBlind, "and when it did");
		} finally {
			Mo2Control.BusyWait = busyWait;
			Procs.FailLooks = 0;
		}
	}

	// the command line's live copy of the log is written by a thread of its own: output nobody reads never holds the run
	static void T_TheLiveLogNeverHoldsTheRun()
	{
		using var f = new FakeMo2();
		var got = new List<string>();
		using (var log = new RunLog(Path.Combine(f.Data, "echo log"))) {
			log.EchoTo(l => {
				lock (got) {
					got.Add(l);
				}
			});
			for (var i = 0; i < 50; ++i) {
				log.Say("line " + i);
			}
		}
		Check(got.Count == 50 && got[0].EndsWith("line 0", StringComparison.Ordinal) && got[49].EndsWith("line 49", StringComparison.Ordinal), $"every line reaches the command line, in order, before the run's end returns ({got.Count})");
		// whatever the reader of the live copy throws stays on its thread - out of memory too: left to leave it, it would end
		// the whole program before the profile is put back (this test would not get past the next lines)
		var reached = 0;
		using (var log = new RunLog(Path.Combine(f.Data, "echo log 3"))) {
			log.EchoTo(_ => {
				Interlocked.Increment(ref reached);
				throw new OutOfMemoryException("no memory for the live copy");
			});
			for (var i = 0; i < 20; ++i) {
				log.Say("line " + i);
			}
		}
		Check(reached >= 1, "the live copy was handed a line");
		Equal(20, File.ReadAllLines(Path.Combine(f.Data, "echo log 3", "log.txt")).Length, "the run's own log has every line, and the program lives on");
		var wait = RunLog.EchoWait;
		RunLog.EchoWait = TimeSpan.FromMilliseconds(300);
		using var stuck = new ManualResetEventSlim(false);
		try {
			var took = Stopwatch.StartNew();
			using (var log = new RunLog(Path.Combine(f.Data, "echo log 2"))) {
				log.EchoTo(_ => stuck.Wait(TimeSpan.FromSeconds(60)));
				for (var i = 0; i < 2500; ++i) {
					log.Say("line " + i);
				}
			}
			Check(took.Elapsed < TimeSpan.FromSeconds(30), "2500 lines written and the log closed while nothing reads the output: " + took.Elapsed);
			Equal(2500, File.ReadAllLines(Path.Combine(f.Data, "echo log 2", "log.txt")).Length, "and the log file has every line");
		} finally {
			stuck.Set();
			RunLog.EchoWait = wait;
		}
	}

	// ------------------------------------------------------------------ what MO2 changed
	static void T_Mo2ChangesReadThePaneTopDown()
	{
		// the file lists the pane bottom-up: a separator sits AFTER its mods; "*" lines are MO2's own, not mods
		var pane = Mo2Changes.Pane(["# generated", "+Mod C", "-Mod B", "+Utilities_separator", "*DLC: Dawnguard", "+Mod A", "-Other_separator", "+Top Mod", ""]);
		Equal("Top Mod|-|Mod A|Other|Mod B|Utilities|Mod C|Utilities", string.Join("|", pane.SelectMany(m => new[] { m.Name, m.Section == "" ? "-" : m.Section })), "top-down, each under its separator");
		Check(pane.Single(m => m.Name == "Mod B").On == false && pane.Single(m => m.Name == "Mod C").On, "ticks read");
		Check(pane.All(m => !m.Name.StartsWith("DLC", StringComparison.Ordinal) && !m.Name.EndsWith("_separator", StringComparison.Ordinal)), "no base game, no separator");
	}

	static void T_Mo2ChangesNameEveryKindOfChange()
	{
		Mo2Changes.PaneMod M(string a_n, bool a_on, string a_s) => new(a_n, a_on, a_s);
		var before = new List<Mo2Changes.PaneMod> { M("A", true, "S1"), M("B", true, "S1"), M("C", true, "S1"), M("D", false, "S1"), M("G", true, "S2") };
		var now = new List<Mo2Changes.PaneMod> { M("B", true, "S1"), M("A", true, "S1"), M("C", false, "S1"), M("D", false, "Other"), M("F", true, "") };
		var got = Mo2Changes.Compare(before, now);
		Check(got.Contains(("F", "added (ticked, under no section)")), "added: " + string.Join("; ", got));
		Check(got.Contains(("G", "removed (was under S2)")), "removed");
		Check(got.Contains(("C", "unticked")), "unticked");
		Equal(1, got.Count(r => r.What.StartsWith("moved within", StringComparison.Ordinal)), "two mods swapped = ONE moved");
		Check(!got.Any(r => r.Mod == "D" && r.What.StartsWith("moved", StringComparison.Ordinal)), "D kept its place: a new section is not a move");
		// a separator moved past mods: their section changes, their place does not - no row
		Equal(0, Mo2Changes.Compare([M("A", true, "S1"), M("B", true, "S1")], [M("A", true, "S0"), M("B", true, "S0")]).Count, "a separator's move is no mod's move");
		// one mod taken from the top to the bottom: only it moved, into its new section
		var moved = Mo2Changes.Compare([M("A", false, "S1"), M("B", true, "S1"), M("C", true, "S1")], [M("B", true, "S1"), M("C", true, "S1"), M("A", false, "Other")]);
		Equal("A: moved from S1 to Other", string.Join("; ", moved.Select(r => $"{r.Mod}: {r.What}")), "one move, both sections named");
		// names as Windows sees folders: a new spelling is the same mod
		Equal(0, Mo2Changes.Compare([M("PG Patch", true, "S")], [M("PG patch", true, "S")]).Count, "same mod, other case");
	}

	static void T_Mo2ChangesRecordGroupAndHide()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var second = Path.Combine(f.Instance, "profiles", "Second");
		Directory.CreateDirectory(second);
		File.Copy(Path.Combine(f.ProfileDir, "modlist.txt"), Path.Combine(second, "modlist.txt"));
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "the first look only starts watching");
		var dir = Mo2Changes.Dir(f.Data, mo2);
		Check(Mo2Changes.WatchedSince(dir) != null && !Path.GetFileName(dir).Contains('_', StringComparison.Ordinal), "watched, its folder named without underscores");
		// the same change in both profiles, a change in one, a mod added to MO2, a test mod
		foreach (var p in new[] { f.ProfileDir, second }) {
			var file = Path.Combine(p, "modlist.txt");
			File.WriteAllText(file, File.ReadAllText(file).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		}
		File.WriteAllText(Path.Combine(second, "modlist.txt"), File.ReadAllText(Path.Combine(second, "modlist.txt")).Replace("+Replaced", "-Replaced", StringComparison.Ordinal));
		f.Mod("Fresh Mod");
		f.Mod("TEST Something");
		var rows = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Check(rows.Any(r => r.Mod == "Base Mod" && r.What == "unticked" && r.Profiles.SequenceEqual(["Second", "Test"])), "one row for both profiles: " + string.Join("; ", rows.Select(r => $"{r.Mod} {r.What} [{string.Join(",", r.Profiles)}]")));
		Check(rows.Any(r => r.Mod == "Replaced" && r.What == "unticked" && r.Profiles.SequenceEqual(["Second"])), "a change in one profile names it alone");
		Check(rows.Any(r => r.Mod == "Fresh Mod" && r.What == "added to Mod Organizer" && r.Profiles.Count == 0), "a mod added to MO2");
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "nothing new on the next look");
		var kept = Mo2Changes.Read(dir);
		Equal(rows.Count, kept.Count, "every row kept on disk");
		Check(kept.All(r => r.Who == Mo2Changes.Outside), "and whose");
		// the view: a profile left out comes off each row (a row with none left goes), a mod name left out goes
		var shown = Mo2Changes.Shown(kept, ["Second"], ["TEST "]);
		Check(shown.Any(r => r.Mod == "Base Mod" && r.Profiles.SequenceEqual(["Test"])), "Second taken off the shared row");
		Check(!shown.Any(r => r.Mod == "Replaced"), "a row only of Second dropped");
		Check(!shown.Any(r => r.Mod.StartsWith("TEST", StringComparison.Ordinal)) && shown.Any(r => r.Mod == "Fresh Mod"), "test mods left out, Mod Organizer's rows kept");
		// a profile's own row is never dropped by a mod-name filter (only by the profiles left out)
		var p3 = Path.Combine(f.Instance, "profiles", "TEST Profile");
		Directory.CreateDirectory(p3);
		File.Copy(Path.Combine(f.ProfileDir, "modlist.txt"), Path.Combine(p3, "modlist.txt"));
		var added = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Check(Mo2Changes.Shown(added, [], ["TEST "]).Any(r => r.What == "profile added" && r.Mod == "TEST Profile"), "a profile named like a hidden mod still shows");
		// one mod spelt two ways in two profiles is ONE row
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), File.ReadAllText(Path.Combine(f.ProfileDir, "modlist.txt")).Replace("+Master Mod", "-Master Mod", StringComparison.Ordinal));
		File.WriteAllText(Path.Combine(second, "modlist.txt"), File.ReadAllText(Path.Combine(second, "modlist.txt")).Replace("+Master Mod", "-master mod", StringComparison.Ordinal));
		var cased = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Equal(1, cased.Count(r => r.What == "unticked" && r.Mod.Equals("Master Mod", StringComparison.OrdinalIgnoreCase)), "one row for both spellings: " + string.Join("; ", cased.Select(r => $"{r.Mod} {r.What} [{string.Join(",", r.Profiles)}]")));
		// a mod list read half-written (empty here) or missing for a known profile: no look at all, nothing written
		var lookBytes = File.ReadAllBytes(Path.Combine(dir, "last look.json"));
		var rowsBytes = File.ReadAllBytes(Path.Combine(dir, "changes.json"));
		var secondList = Path.Combine(second, "modlist.txt");
		var keepList = File.ReadAllBytes(secondList);
		File.WriteAllBytes(secondList, []);
		Throws<IOException>(() => Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside), "an empty mod list is not a look");
		File.Delete(secondList);
		Throws<IOException>(() => Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside), "a known profile's missing mod list is not a removed profile");
		Check(File.ReadAllBytes(Path.Combine(dir, "last look.json")).AsSpan().SequenceEqual(lookBytes) && File.ReadAllBytes(Path.Combine(dir, "changes.json")).AsSpan().SequenceEqual(rowsBytes), "nothing written by a look that did not read");
		File.WriteAllBytes(secondList, keepList);
		// a look stopped between its rows and its own file: finished next time, its rows kept once
		File.WriteAllText(f.ProfileDir + "\\modlist.txt", File.ReadAllText(f.ProfileDir + "\\modlist.txt").Replace("+Output A", "-Output A", StringComparison.Ordinal));
		var lastLook = File.ReadAllBytes(Path.Combine(dir, "last look.json"));
		var once = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Check(once.Any(r => r.Mod == "Output A"), "the change seen");
		File.Copy(Path.Combine(dir, "last look.json"), Path.Combine(dir, "next look.json"));
		File.WriteAllBytes(Path.Combine(dir, "last look.json"), lastLook);
		var total = Mo2Changes.Read(dir).Count;
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "the stopped look finished, not seen again");
		Equal(total, Mo2Changes.Read(dir).Count, "no row kept twice");
		Check(!File.Exists(Path.Combine(dir, "next look.json")), "the next look became the last one");
		// stopped before its rows were kept (no row carries its time): dropped, and the change it saw is found again
		File.WriteAllText(f.ProfileDir + "\\modlist.txt", File.ReadAllText(f.ProfileDir + "\\modlist.txt").Replace("+Output B", "-Output B", StringComparison.Ordinal));
		var unkept = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "last look.json")))!.AsObject();
		unkept["taken"] = "2001-01-01T00:00:00.0000000";
		foreach (var m in unkept["profiles"]!["Test"]!.AsArray().OfType<JsonArray>().Where(m => m[0]!.ToString() == "Output B")) {
			m[1] = false;
		}
		File.WriteAllText(Path.Combine(dir, "next look.json"), unkept.ToJsonString());
		Check(Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Any(r => r.Mod == "Output B" && r.What == "unticked"), "a look whose rows were never kept is dropped: the change is found");
		// a damaged history is never written over: set aside whole, and a new one starts
		var history = Path.Combine(dir, "changes.json");
		File.WriteAllText(history, "[ { not json");
		File.WriteAllText(f.ProfileDir + "\\modlist.txt", File.ReadAllText(f.ProfileDir + "\\modlist.txt").Replace("-Base Mod", "+Base Mod", StringComparison.Ordinal));
		Check(Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count > 0, "(a change to keep)");
		var aside = Directory.GetFiles(dir, "changes unreadable *.json");
		Check(aside.Length == 1 && File.ReadAllText(aside[0]) == "[ { not json", "the damaged history kept as it was, beside the new one");
		Check(Mo2Changes.Read(dir).Count > 0, "the new history reads");
		// a damaged last look starts the watch again - never a failure
		var keptNow = Mo2Changes.Read(dir).Count;
		File.WriteAllText(Path.Combine(dir, "last look.json"), "{ not json");
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "a damaged look starts over");
		Equal(keptNow, Mo2Changes.Read(dir).Count, "the rows already kept stay");
	}

	// "Look now" and the command line's --look hold the run lock from their check to their end: never beside a run
	static void T_OutsideLookNeverBesideARun()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		using var taken = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var t = new Thread(() => {
			using var hold = Runner.HoldIfFree();
			taken.Set();
			release.Wait();
		});
		t.Start();
		taken.Wait();
		try {
			Equal<int?>(null, Mo2Changes.RecordOutside(mo2, f.Data), "no look while a run holds the lock");
			Check(!Directory.Exists(Mo2Changes.Dir(f.Data, mo2)) || Directory.GetFiles(Mo2Changes.Dir(f.Data, mo2)).Length == 0, "nothing written");
		} finally {
			release.Set();
			t.Join();
		}
		Equal<int?>(0, Mo2Changes.RecordOutside(mo2, f.Data), "free again: the look is taken");
	}

	static void T_RunNotesWhatItChangedApartFromWhatWasChangedOutside()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		// the player unticks a mod in MO2, then a run makes a new output mod
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "New Output", "New.esp")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "New Output" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		var rows = Mo2Changes.Read(Mo2Changes.Dir(f.Data, mo2));
		var text = string.Join("; ", rows.Select(x => $"{x.Mod} {x.What} ({x.Who})"));
		Check(rows.Any(x => x.Mod == "Base Mod" && x.What == "unticked" && x.Who == Mo2Changes.Outside), "the player's change, as made outside the runner: " + text);
		Check(rows.Any(x => x.Mod == "New Output" && x.What == "added to Mod Organizer" && x.Who.StartsWith("the run of", StringComparison.Ordinal)), "the run's new mod, as the run's: " + text);
		Check(rows.Any(x => x.Mod == "New Output" && x.What.StartsWith("added (ticked", StringComparison.Ordinal) && x.Who.StartsWith("the run of", StringComparison.Ordinal)), "ticked in the profile by the run: " + text);
		Check(!rows.Any(x => x.Mod is "Output A" or "Output B"), "outputs switched off and back on during the run are no net change: " + text);

		// the setup's own before-run command changes MO2: the run's change, not one made outside it
		var changed = Path.Combine(f.Root, "modlist changed.txt");
		File.WriteAllText(changed, File.ReadAllText(list).Replace("+Master Mod", "-Master Mod", StringComparison.Ordinal));
		var s = Setup.Load(f.SetupFile);
		s.Root["beforeRun"] = new JsonArray($"copy /y \"{changed}\" \"{list}\" >nul");
		s.Save();
		r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		rows = Mo2Changes.Read(Mo2Changes.Dir(f.Data, mo2));
		text = string.Join("; ", rows.Select(x => $"{x.Mod} {x.What} ({x.Who})"));
		Check(rows.Any(x => x.Mod == "Master Mod" && x.What == "unticked" && x.Who.StartsWith("the run of", StringComparison.Ordinal)), "the before-run command's change is the run's: " + text);
		Check(!rows.Any(x => x.Mod == "Master Mod" && x.Who == Mo2Changes.Outside), "never as made outside the runner: " + text);
	}

	// a run cut off whose setup is gone since: the profile is put back all the same - the run's record names its Mod Organizer and
	// its profile - so no start is refused for a put-back that could never be made
	static void T_AProfileIsPutBackWithoutItsSetup()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var before = f.Text("modlist.txt");
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		Check(f.Text("modlist.txt") != before, "the cut-off run left its output switched off");
		File.Delete(f.SetupFile);
		var r = Runner.Restore(Journal.Interrupted(Runner.RunsDir(f.Data))!);
		Equal(RunState.Restored, r.State, "put back without its setup: " + r.Verdict);
		Equal(before, f.Text("modlist.txt"), "the mod list is as it was before the run");
		Check(Journal.Interrupted(Runner.RunsDir(f.Data)) == null, "nothing is left to put back");
	}

	// a put-back after a crash: what changed before it (the cut-off run, the player since) is noted apart from the put-back's own
	static void T_PutBackNotesOnlyItsOwnChanges()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		Equal(RunState.Restored, Runner.Restore(Journal.Interrupted(Runner.RunsDir(f.Data))!).State, "restored");
		var rows = Mo2Changes.Read(Mo2Changes.Dir(f.Data, mo2));
		var text = string.Join("; ", rows.Select(x => $"{x.Mod} {x.What} ({x.Who})"));
		Check(rows.Any(x => x.Mod == "Output A" && x.What == "unticked" && x.Who.StartsWith("seen before putting back", StringComparison.Ordinal)), "the cut-off run's change noted before the put-back: " + text);
		Check(rows.Any(x => x.Mod == "Output A" && x.What == "ticked" && x.Who.StartsWith("putting back", StringComparison.Ordinal)), "the put-back's own change: " + text);
		Check(!rows.Any(x => x.What == "unticked" && x.Who.StartsWith("putting back", StringComparison.Ordinal)), "nothing from before it named as the put-back's: " + text);
	}

	static void T_FailedStepPutsItsOldOutputBack()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			StepKit.ParkMod(ctx, "synthesis", "Output B");
			File.WriteAllText(Path.Combine(ctx.Mo2.ModDir("Output B"), "half.txt"), "half");
			throw new StepFailedException("the tool crashed");
		}));
		StepRegistry.Replace(new FakeStep("pgpatcher", (ctx, s) => throw new InvalidOperationException("must not run")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Output B" }), ("pgpatcher", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "failed");
		Check(r.Verdict.Contains("Synthesis", StringComparison.Ordinal) && r.Verdict.Contains("the tool crashed", StringComparison.Ordinal), "the verdict names the step and why: " + r.Verdict);
		Check(File.Exists(Path.Combine(f.Mods, "Output B", "OutB.esp")), "the old output is back in its mod");
		Check(!File.Exists(Path.Combine(f.Mods, "Output B", "half.txt")), "the half-written output is moved out");
		var p = ProfileOf(f);
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "the profile as before, the finished output on");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Equal(StepState.Skipped, j.Step("pgpatcher")!.State, "the step after it never ran");
	}

	static void T_StopAfterThisStep()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			Commands.Send(ctx.Journal.Folder, "stop after");
			Thread.Sleep(1200);
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		var ran = false;
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => ran = true));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Stopped, r.State, "stopped: " + r.Verdict);
		Check(!ran, "the next step did not run");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")), "the running step finished");
	}

	static void T_StopNowIsSafe()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "landscapefixer", "Output A");
			Commands.Send(ctx.Journal.Folder, "stop");
			for (int i = 0; i < 50; ++i) {
				ctx.Cancel.ThrowIfCancellationRequested();
				Thread.Sleep(100);
			}
			throw new StepFailedException("the stop was not seen");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Stopped, r.State, "stopped: " + r.Verdict);
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "the old output back");
		Check(ProfileOf(f).IsOn("Output A"), "the profile back");
	}

	static void T_CrashedRunIsRestored()
	{
		using var f = new FakeMo2();
		// a run that "crashed": the record says running, its owner is gone, outputs off, one output parked
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "landscapefixer", "Output A");
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		var j = Journal.Interrupted(Runner.RunsDir(f.Data));
		Check(j != null, "the unfinished run is found");
		Check(!File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "(the crash left the output parked)");
		var refused = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(refused.Verdict.Contains("did not finish", StringComparison.Ordinal), "a new run is refused until it is put back: " + refused.Verdict);
		var r = Runner.Restore(j!);
		Equal(RunState.Restored, r.State, "restored: " + r.Verdict);
		Check(Journal.Load(j!.Folder).Steps.All(s => s.State != StepState.Stopped || s.Ended != null), "the step the crash cut off is closed (it never reads as still running)");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "the parked output is back");
		var p = ProfileOf(f);
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "the profile as it was");
	}

	// the put-back after a crash keeps what the player changed in Mod Organizer while the run went on and after it was cut off -
	// the run's last expectation is read back from its folder; the run's own switches are still undone
	static void T_RestoreKeepsThePlayersChanges()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "landscapefixer", "Output A");
			// the player unticks a mod of theirs in Mod Organizer while the step works
			var list = Path.Combine(f.ProfileDir, "modlist.txt");
			File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		var j = Journal.Interrupted(Runner.RunsDir(f.Data));
		Check(j != null && File.Exists(Path.Combine(j.Folder, Profile.WatchFileName)), "the run kept what it wrote in its folder");
		// and after the crash, before Restore, the player installs and ticks a new mod
		f.Mod("Players New");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Players New\r\n" + f.Text("modlist.txt"));
		var r = Runner.Restore(j!);
		Equal(RunState.Restored, r.State, "restored: " + r.Verdict);
		var p = ProfileOf(f);
		Check(!p.IsOn("Base Mod"), "the mod the player unticked during the run stays off");
		Check(p.IsOn("Players New"), "the mod the player ticked after the crash stays on");
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "the run's own switches are undone - the profile as it was");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "the parked output is back");
	}

	// a file a step changed (a tool's settings): put back by the step's failure, and after a crash by Restore
	static void T_KeptFilesComeBackAfterAFailureAndACrash()
	{
		using var f = new FakeMo2();
		var settings = Path.Combine(f.Root, "tool settings.json");
		var flag = Path.Combine(f.Root, "flag.txt");
		File.WriteAllText(settings, "the player's own");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.KeepFile(ctx, settings);
			StepKit.KeepFile(ctx, flag);
			File.WriteAllText(settings, "pointed at the output");
			File.WriteAllText(flag, "");
			throw new StepFailedException("the tool crashed");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "failed");
		Equal("the player's own", File.ReadAllText(settings), "the changed file is back");
		Check(!File.Exists(flag), "the file it made is gone again");

		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.KeepFile(ctx, settings);
			File.WriteAllText(settings, "pointed at the output");
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		Equal("pointed at the output", File.ReadAllText(settings), "(the crash left it changed)");
		Equal(RunState.Restored, Runner.Restore(Journal.Interrupted(Runner.RunsDir(f.Data))!).State, "restored");
		Equal("the player's own", File.ReadAllText(settings), "Restore put the file back");
	}

	// what a failed step left in MO2's overwrite would win over the output put back: moved into the run's backup
	static void T_FailedStepsOverwriteFilesGoToTheBackup()
	{
		using var f = new FakeMo2();
		Directory.CreateDirectory(f.Overwrite);
		File.WriteAllText(Path.Combine(f.Overwrite, "was there.ini"), "old");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			Directory.CreateDirectory(Path.Combine(f.Overwrite, "meshes"));
			File.WriteAllText(Path.Combine(f.Overwrite, "meshes", "half.nif"), "half");
			throw new StepFailedException("the tool crashed");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(!File.Exists(Path.Combine(f.Overwrite, "meshes", "half.nif")), "the step's new file left overwrite");
		Check(File.Exists(Path.Combine(f.Overwrite, "was there.ini")), "what was there before stays");
		Check(Directory.EnumerateFiles(f.Backups, "half.nif", SearchOption.AllDirectories).Any(), "it is in the run's backup");
	}

	// a setup that spells a mod differently from the list ("output a" / "Output A") switches the same mod
	static void T_ModNamesIgnoreCase()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		var changed = p.SetMods([], ["output a"]);
		Check(changed.ContainsKey("output a") && !p.IsOn("Output A"), "unticked through the other spelling");
		Check(f.Text("modlist.txt").Contains("-Output A", StringComparison.Ordinal), "the list keeps its own spelling");
		p.EnsureMod("OUTPUT A");
		Equal(1, f.Text("modlist.txt").Split('\n').Count(l => l.TrimEnd().Equals("-Output A", StringComparison.OrdinalIgnoreCase)), "no second line for the same folder");
	}

	// MO2 is never closed under a program the player started from it (MO2 would end it)
	static void T_CloseWaitsForThePlayersOwnProgram()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var keep = Mo2Control.BusyWait;
		Mo2Control.BusyWait = TimeSpan.FromSeconds(1);
		try {
			var c = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => true, ForeignProbe = () => [(4242, "SSEEdit.exe")] };
			try {
				c.Close();
				Check(false, "MO2 was closed under the player's xEdit");
			} catch (Mo2BusyException e) {
				Check(e.Message.Contains("SSEEdit.exe", StringComparison.Ordinal), "the program is named: " + e.Message);
			}
		} finally {
			Mo2Control.BusyWait = keep;
		}
	}

	// a Mod Organizer that does not close: a Stop ends the wait for it within a moment, not at the close's five-minute limit
	// (the tools box's Stop reaches this wait). The Stop comes only once the close's own wait has begun - the second look at
	// whether Mod Organizer runs is that wait's first - so the checks before it cannot end the test early
	static void T_AStopEndsTheWaitForModOrganizerToClose()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var game = Mo2Control.GameProbe;
		Mo2Control.GameProbe = () => [];
		try {
			using var cts = new CancellationTokenSource();
			var looks = 0;
			var c = new Mo2Control(mo2, "Test", RunLog.Null) {
				RunningProbe = () => {
					if (++looks == 2) {
						cts.Cancel();
					}
					return true;
				},
				ForeignProbe = () => [], RepackProbe = () => [],
			};
			var sw = Stopwatch.StartNew();
			Throws<OperationCanceledException>(() => c.Close(null, cts.Token), "a Stop while Mod Organizer will not close");
			Check(looks >= 2, $"the Stop came inside the close's own wait ({looks} look(s))");
			Check(sw.Elapsed < TimeSpan.FromSeconds(10), $"and it ended the wait at once ({sw.Elapsed.TotalSeconds:0.0} s)");
		} finally {
			Mo2Control.GameProbe = game;
		}
	}

	// a run record that holds no record ("null") is passed over by every list of the runs, as one that does not read is - it
	// threw out of the list, and with it out of the window's look and the status command; a start still stops at it when it is the newest
	static void T_ARecordThatHoldsNothingIsPassedOver()
	{
		using var f = new FakeMo2();
		var runs = Runner.RunsDir(f.Data);
		var good = Path.Combine(runs, "2026-01-01 00-00-00 Test");
		var empty = Path.Combine(runs, "2026-01-02 00-00-00 Test");
		Directory.CreateDirectory(good);
		Directory.CreateDirectory(empty);
		new Journal { Folder = good, SetupFile = f.SetupFile, State = RunState.Finished }.Save();
		File.WriteAllText(Path.Combine(empty, "journal.json"), "null");
		Equal(1, Journal.All(runs).Count(), "the list holds the one that reads");
		Equal(1, new JournalCache(runs).Read().Count, "and so does the window's cached list");
		Check(Journal.NewestUnreadable(runs) is { } dim && Files.SamePath(dim.Folder, empty), "the newest record that holds nothing still stops a start");
	}

	// an older record that does not read is passed over by the lists, but never taken as a run that needs nothing: what deletes on
	// the strength of the records - the old backups' clean-up, removing the program's data - leaves everything as it is
	static void T_AnUnreadableRecordStopsEveryDelete()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()));
		var runs = Runner.RunsDir(f.Data);
		var damaged = Path.Combine(runs, "2026-01-01 00-00-00 Test");
		var good = Path.Combine(runs, "2026-01-02 00-00-00 Test");
		Directory.CreateDirectory(damaged);
		Directory.CreateDirectory(good);
		File.WriteAllText(Path.Combine(damaged, "journal.json"), "{ not a record");
		new Journal { Folder = good, SetupFile = f.SetupFile, State = RunState.Finished }.Save();
		foreach (var w in new[] { "2026-11-01 20-00-00", "2026-11-02 20-00-00", "2026-11-03 20-00-00" }) {
			Directory.CreateDirectory(Path.Combine(setup.BackupDir, w + " " + setup.Name));
		}
		Check(Journal.NewestUnreadable(runs) == null, "the newest record reads: a start is not stopped by an older one");
		var (read, unread) = Journal.AllOrUnreadable(runs);
		Check(read.Count == 1 && Files.SamePath(read[0].Folder, good) && unread is { } bad && Files.SamePath(bad.Folder, damaged), "one read gives the records that read and names the older one that does not");
		Equal(0, Runner.PruneBackupsOf(setup, runs, null, 1, RunLog.Null).Count, "no old backup is removed while a record does not read");
		Equal(3, Runner.BackupsOf(setup).Count, "all three are still there");
		Check(DataRemoval.Make(f.Data, false).Refused?.Contains("could not be read", StringComparison.Ordinal) == true, "and the program's data is not removed");
	}

	// a run record whose PID now belongs to another program (after a crash and a restart) is not "alive"
	static void T_ReusedPidIsNotTheRun()
	{
		var me = Environment.ProcessId;
		var j = new Journal { Pid = me, PidStarted = StartedUtc() };
		Check(j.OwnerAlive, "the real owner is alive");
		j.PidStarted = j.PidStarted.GetValueOrDefault().AddMinutes(-5);
		Check(!j.OwnerAlive, "the same PID with another start time is not the run");
	}

	// one argument on a Windows command line, read back the way Windows splits it
	static void T_QuoteRoundTripsThroughWindows()
	{
		foreach (var a in new[] { @"D:\LOD\", @"C:\Program Files\x", "say \"hi\"", @"a\\""b", @"ends\\", "plain", "" }) {
			var line = "x.exe " + Mo2Control.Quote(a) + " next";
			var got = Argv(line);
			Equal(3, got.Length, "three arguments for " + a);
			Equal(a, got[1], "the argument comes back");
			Equal("next", got[2], "the next argument is not swallowed");
		}
	}

	[System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	static extern nint CommandLineToArgvW(string a_line, out int a_count);

	[System.Runtime.InteropServices.DllImport("kernel32.dll")]
	static extern nint LocalFree(nint a_mem);

	static string[] Argv(string a_line)
	{
		var p = CommandLineToArgvW(a_line, out var n);
		try {
			return Enumerable.Range(0, n).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(p, i * nint.Size))!).ToArray();
		} finally {
			LocalFree(p);
		}
	}

	// the shader cache is done when its log says remaining=0 - the last "remaining=" line is what counts
	static void T_ShaderLogRemainingIsRead()
	{
		using var f = new FakeMo2();
		var log = Path.Combine(f.Root, "BottledShaders.log");
		File.WriteAllText(log, "[2026-10-26 17:50:34.017] [info] [ShaderTiming] x remaining=2707\n[17:51:00.000] [info] [ShaderTiming] y remaining=0\n");
		Equal(0, InGame.LastRemaining(log), "the last line wins");
		File.WriteAllText(log, "[info] Shader compilation started\n");
		Equal(null, InGame.LastRemaining(log), "no ShaderTiming lines = unknown (only then may quiet end it)");
	}

	// an emptied setting is not set (its default applies); a relative folder is refused (it would follow the starting folder)
	static void T_EmptySettingsAndRelativePaths()
	{
		var o = new JsonObject { ["args"] = "", ["image"] = "  ", ["executable"] = "X" };
		Equal(null, Setup.Str(o, "args"), "an emptied setting counts as not set");
		Equal(null, new StepSettings(o).Str("image"), "spaces only, too");
		Equal("X", Setup.Str(o, "executable"), "a real value stays");
		using var f = new FakeMo2();
		f.WriteSetup(("pandora", new JsonObject { ["output_mod"] = "Output A", ["output_dir"] = @"scratch\Pandora" }));
		var s = Setup.Load(f.SetupFile);
		Check(s.Problems().Any(p => p.Contains("whole path", StringComparison.Ordinal)), "a relative output folder is a problem: " + string.Join("; ", s.Problems()));
	}

	// the profile's own save folder (sLocalSavePath in its Skyrim.ini when it keeps its own INIs)
	static void T_SavesFollowTheProfilesSavePath()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		File.WriteAllText(Path.Combine(p.Dir, "settings.ini"), "[General]\r\nLocalSettings=true\r\n");
		File.WriteAllText(Path.Combine(p.Dir, "Skyrim.ini"), "[General]\r\nsLocalSavePath=Saves\\MO2 Test\\\r\n");
		Check(InGame.SavesDir(p).EndsWith(@"Skyrim Special Edition\Saves\MO2 Test", StringComparison.OrdinalIgnoreCase), "the profile's own save path: " + InGame.SavesDir(p));
		File.WriteAllText(Path.Combine(p.Dir, "Skyrim.ini"), "[General]\r\n");
		Check(InGame.SavesDir(p).EndsWith(@"Skyrim Special Edition\Saves", StringComparison.OrdinalIgnoreCase), "none set: the game's own Saves");
	}

	// a global instance (%LOCALAPPDATA%\ModOrganizer\<name>) is named on MO2's command line; a portable one is not
	static void T_GlobalInstanceIsNamed()
	{
		using var f = new FakeMo2();
		Equal(null, Mo2Instance.Load(f.Exe, f.Instance).GlobalName, "portable: no -i");
	}

	// a tool's version on its page against the player's copy, written the many ways tools write them
	static void T_ToolVersionsCompare()
	{
		Check(!ToolUpdates.IsNewer("2.3.1.0", "2.3.1"), "2.3.1.0 = 2.3.1");
		Check(!ToolUpdates.IsNewer("5.0.0.0b-", "5.0.0-beta"), "5.0.0.0b- = 5.0.0-beta");
		Check(!ToolUpdates.IsNewer("3.0.0.217", "Alpha-217"), "DynDOLOD's file version 3.0.0.217 = its page's Alpha-217");
		Check(ToolUpdates.IsNewer("3.0.0.217", "Alpha-218"), "Alpha-218 is newer");
		Check(ToolUpdates.IsNewer("1.0.4.0", "1.0.6"), "1.0.6 is newer than 1.0.4");
		Check(!ToolUpdates.IsNewer("0.2.54.0", "0.2.11"), "an older number on the page is not newer");
		Check(!ToolUpdates.IsNewer("", "1.0"), "no version of ours: never called newer");
		Check(ToolUpdates.IsNewer("1.9.2", "2.0"), "2.0 (its zero trimmed) is newer than 1.9.2");
		Check(!ToolUpdates.IsNewer("2.1.0", "2.0"), "2.0 is not newer than 2.1.0");
		Check(ToolUpdates.IsNewer("1.1.2", "1.2"), "1.2 is newer than 1.1.2, though its numbers end it");
		Check(ToolUpdates.IsNewer("1.2.1", "2.1"), "2.1 is newer than 1.2.1");
		Check(!ToolUpdates.IsNewer("1.2", "1.1.2"), "and 1.1.2 is not newer than 1.2");
		// the line under the rows counts every tool a row names (TexGen and DynDOLOD share one row)
		var both = new ToolUpdates.Result("texgen", "TexGen / DynDOLOD", "3.0", "", false, "page", "could not ask its page: x", 2);
		var one = new ToolUpdates.Result("pgpatcher", "PGPatcher", "1.0", "1.0", false, "page2", null);
		Check(ToolUpdates.Summary([both, one]).StartsWith("2 tool(s) could not be checked", StringComparison.Ordinal), "a shared row's two tools both counted: " + ToolUpdates.Summary([both, one]));
		Check(ToolUpdates.Summary([both with { Problem = null, Newer = true }, one]).StartsWith("2 tool(s) have a newer version", StringComparison.Ordinal), "both tools of a shared row newer");
		Equal("Every tool is up to date.", ToolUpdates.Summary([one]), "nothing newer, nothing unchecked");
		Check(ToolUpdates.SummaryParts([both, one]) is (var withPlace, 2) && withPlace.Contains("{0}", StringComparison.Ordinal) && ToolUpdates.SummaryParts([one]).Count == null,
			"the same summary with a place for its number, for the window's language");
	}

	// the settings a tool's own window wrote: found, kept, put in place for a run and put back after
	static void T_LearnedSettingsKeepAndApply()
	{
		using var f = new FakeMo2();
		var tool = Path.Combine(f.Root, "Tools", "SomeTool");
		Directory.CreateDirectory(Path.Combine(tool, "cfg"));
		Directory.CreateDirectory(Path.Combine(tool, "Logs"));
		var settings = Path.Combine(tool, "cfg", "settings.json");
		File.WriteAllText(settings, "{\"a\": 1}");
		File.WriteAllText(Path.Combine(tool, "untouched.ini"), "x=1");
		var before = ToolSettings.Scan([tool], []);
		Thread.Sleep(50);
		File.WriteAllText(settings, "{\"a\": 2, \"set\": \"by its window\"}");
		File.WriteAllText(Path.Combine(tool, "new.ini"), "y=2");
		File.WriteAllText(Path.Combine(tool, "Logs", "run.json"), "{}");
		File.WriteAllText(Path.Combine(tool, "notes.txt"), "not a settings file");
		var changed = ToolSettings.Changed(before, ToolSettings.Scan([tool], []));
		Equal(2, changed.Count, "the two settings files it wrote (not its log, not a text file, not one it left alone): " + string.Join(", ", changed));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A", ["executable"] = "SomeTool" }));
		// (the tool stands in Mod Organizer's list, as every tool whose settings were learned does: its folder is where they go)
		var ini = Path.Combine(f.Instance, "ModOrganizer.ini");
		File.WriteAllText(ini, File.ReadAllText(ini).Replace("[customExecutables]\r\nsize=1\r\n", "[customExecutables]\r\nsize=2\r\n" +
			$"2\\title=SomeTool\r\n2\\binary={Path.Combine(tool, "SomeTool.exe").Replace('\\', '/')}\r\n2\\arguments=\r\n", StringComparison.Ordinal));
		var setup = Setup.Load(f.SetupFile);
		var l = ToolSettings.Keep(setup, "landscapefixer", "SomeTool", changed);
		Equal(2, ToolSettings.Load(setup, "landscapefixer")!.Files.Count, "kept with the setup");
		// the player changes the tool by hand since: a run still uses the learned settings, and puts the player's back after
		File.WriteAllText(settings, "{\"a\": 3}");
		var seen = "";
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			seen = File.ReadAllText(settings);
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		Check(seen.Contains("by its window", StringComparison.Ordinal), "the run used the learned settings: " + seen);
		Equal("{\"a\": 3}", File.ReadAllText(settings), "the player's own settings are back after the run");
		Equal(2, l.Files.Count, "(learned set)");
		// a record that names a file anywhere but the tool's own folders (edited by hand, or learned before the tool moved) is
		// refused before a byte is written there
		var elsewhere = Path.Combine(f.Root, "Elsewhere", "settings.json");
		Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
		File.WriteAllText(elsewhere, "theirs");
		ToolSettings.Keep(setup, "landscapefixer", "SomeTool", [elsewhere]);
		File.WriteAllText(elsewhere, "theirs, changed since");
		var found = Preflight.Run(setup, Mo2Instance.Load(f.Exe, f.Instance), ["landscapefixer"]);
		Check(found.Any(i => i.Blocking && i.Text.Contains("outside their folder", StringComparison.Ordinal) && RunnerErrors.Find(i.Text)?.Code == "TR-413"),
			"the check before a run says it first: " + string.Join(" | ", found.Select(i => i.Text)));
		var refused = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(refused.State != RunState.Finished && refused.Verdict.Contains("outside their folder", StringComparison.Ordinal), "and a run does not start, saying so: " + refused.Verdict);
		// (the step itself holds the same line, for a record changed after the check)
		var late = StepContext(f, "landscapefixer");
		using var lateLog = late.Log;
		Throws<StepFailedException>(() => ToolSettings.Apply(late, "landscapefixer"), "the step refuses it too");
		Equal("theirs, changed since", File.ReadAllText(elsewhere), "and the file elsewhere is as it was");
	}

	// With a game or another Mod Organizer open, what holds Mod Organizer is asked about before anything waits, the wait says
	// what it is on, and it can always be stopped.
	static void T_AToolThatCannotOpenYetIsAskedAboutAndItsWaitStops()
	{
		// the words for what holds Mod Organizer open
		Check(Mo2Control.WhyBusy([], [], [], [], false) == null, "nothing holds it: no words, the tool opens at once");
		Equal("the game is running (SkyrimSE.exe)", Mo2Control.WhyBusy([], ["SkyrimSE.exe"], [], [], false), "the game");
		Check(Mo2Control.WhyBusy(["a mod.7z"], ["SkyrimSE.exe"], [], [], false)!.StartsWith("Mod Organizer is downloading", StringComparison.Ordinal), "a download first");
		Check(Mo2Control.WhyBusy([], [], ["xEdit.exe", "xEdit.exe"], [], false) == "a program you started runs under Mod Organizer (xEdit.exe)", "a program of the player's, named once");
		Check(Mo2Control.WhyBusy([], [], [], null, false)!.StartsWith("Windows would not list", StringComparison.Ordinal), "a look Windows refused is never 'nothing'");
		Check(Mo2Control.WhyBusy([], [], [], [], true)!.StartsWith("Windows would not list", StringComparison.Ordinal), "nor is a blind one");

		var setup = Setup.FromJson(new JsonObject { ["name"] = "Test", ["steps"] = new JsonArray() }, "Test.json");
		var lookWas = ToolOpen.BusyLook;
		var learnWas = ToolOpen.LearnWith;
		try {
			var learnedCalls = 0;
			ToolOpen.BusyLook = (_, _) => "the game is running (SkyrimSE.exe)";
			ToolOpen.LearnWith = (_, _, _, _, _, _) => {
				++learnedCalls;
				return new ToolSettings.Learned { Step = "texgen", Tool = "TexGen" };
			};
			// "Not now": nothing waits and nothing opens
			string? asked = null;
			using (var o = new ToolOpen()) {
				var r = o.Go(setup, "texgen", why => {
					asked = why;
					return false;
				}, _ => { }).GetAwaiter().GetResult();
				Check(r.How == ToolOpen.Ending.NotNow && learnedCalls == 0, "the player chose not to wait: nothing was opened or waited on");
				Equal("the game is running (SkyrimSE.exe)", asked, "and they were told what holds it");
				Check(ToolRunner.App.ToolsDialog.Outcome("TexGen", r).Contains("the game is running", StringComparison.Ordinal), "the tool's row says why: " + ToolRunner.App.ToolsDialog.Outcome("TexGen", r));
			}
			// "Wait for it", the wait stopped before the tool's window is up: it ends at once, and nothing was opened
			ToolOpen.LearnWith = (_, _, log, ct, _, _) => {
				log.Warn("the game is running (SkyrimSE.exe) - waiting for it to end before closing Mod Organizer (up to 30 minutes)");
				ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
				ct.ThrowIfCancellationRequested();
				return new ToolSettings.Learned { Step = "texgen", Tool = "TexGen" };
			};
			using (var o = new ToolOpen()) {
				var heard = new List<string>();
				var going = o.Go(setup, "texgen", _ => true, line => {
					lock (heard) {
						heard.Add(line);
					}
				});
				for (var w = System.Diagnostics.Stopwatch.StartNew(); w.Elapsed < TimeSpan.FromSeconds(10) && heard.Count == 0;) {
					Thread.Sleep(20);
				}
				Check(heard.Any(h => h.Contains("waiting for it to end", StringComparison.Ordinal)), "the wait says what it is on, where the player looks");
				var clock = System.Diagnostics.Stopwatch.StartNew();
				o.Stop();
				var r = going.GetAwaiter().GetResult();
				Check(r.How == ToolOpen.Ending.Stopped && !o.Opened && clock.Elapsed < TimeSpan.FromSeconds(5), $"the wait stopped at once ({clock.Elapsed.TotalSeconds:0.0} s)");
				Check(r.Why!.Contains("not opened", StringComparison.Ordinal), "and says nothing was opened: " + r.Why);
			}
			// the tool's window already up, the wait stopped on the player's word: it ends, and says the window stays open
			ToolOpen.BusyLook = (_, _) => null;
			ToolOpen.LearnWith = (_, _, _, ct, _, opened) => {
				opened();
				ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
				ct.ThrowIfCancellationRequested();
				return new ToolSettings.Learned();
			};
			using (var o = new ToolOpen()) {
				var asks = 0;
				var going = o.Go(setup, "texgen", _ => {
					++asks;
					return true;
				}, _ => { });
				for (var w = System.Diagnostics.Stopwatch.StartNew(); w.Elapsed < TimeSpan.FromSeconds(10) && !o.Opened;) {
					Thread.Sleep(20);
				}
				// once its window is up, a stop is never made without the player's word
				Check(!o.StopIfNotOpen() && !o.Stopping, "an open tool is not stopped without asking");
				o.Stop();
				var r = going.GetAwaiter().GetResult();
				Check(asks == 0, "nothing holding Mod Organizer: no question, the tool opens");
				Check(r.How == ToolOpen.Ending.Stopped && o.Opened && r.Why!.Contains("stays open", StringComparison.Ordinal), "stopped after its window was up: " + r.Why);
			}
			// nothing in the way: the settings are kept as before
			ToolOpen.LearnWith = (_, _, _, _, _, opened) => {
				opened();
				return new ToolSettings.Learned { Step = "texgen", Tool = "TexGen", Files = [new() { Live = "a.ini", Stored = "a.ini" }] };
			};
			using (var o = new ToolOpen()) {
				var r = o.Go(setup, "texgen", _ => true, _ => { }).GetAwaiter().GetResult();
				Check(r.How == ToolOpen.Ending.Learned && r.Learned!.Files.Count == 1, "a tool set up and closed: its settings kept");
			}
			// stopped while it still looked at what holds Mod Organizer (the box closed then): nothing is asked, nothing opened
			using (var looking = new ManualResetEventSlim(false))
			using (var o = new ToolOpen()) {
				var go = new ManualResetEventSlim(false);
				// (a look that a Stop ends where it stands, as the real one now does)
				ToolOpen.BusyLook = (_, stop) => {
					looking.Set();
					go.Wait(TimeSpan.FromSeconds(10), stop);
					return "the game is running (SkyrimSE.exe)";
				};
				var asks = 0;
				var going = o.Go(setup, "texgen", _ => {
					++asks;
					return true;
				}, _ => { });
				looking.Wait(TimeSpan.FromSeconds(10));
				o.Stop();
				go.Set();
				var r = going.GetAwaiter().GetResult();
				Check(r.How == ToolOpen.Ending.Stopped && asks == 0 && !o.Opened, "stopped during the look: no question, nothing opened");
				go.Dispose();
			}
			// a look that fails is said, never a wait
			ToolOpen.BusyLook = (_, _) => throw new IOException("the Mod Organizer folder does not read");
			using (var o = new ToolOpen()) {
				var r = o.Go(setup, "texgen", _ => true, _ => { }).GetAwaiter().GetResult();
				Check(r.How == ToolOpen.Ending.Failed && r.Why!.Contains("does not read", StringComparison.Ordinal), "a look that failed is said as it is");
			}
		} finally {
			ToolOpen.BusyLook = lookWas;
			ToolOpen.LearnWith = learnWas;
		}
	}

	// each patcher set up by hand before a setup's first run, and again whenever wanted - the box
	// lists the setup's tools that have a window of their own, and shows by itself once, before the first run only
	static void T_TheToolsOfASetupAreOfferedBeforeItsFirstRun()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("xlodgen", new JsonObject { ["executable"] = "xLODGen", ["output_mod"] = "Output A" }),
			("synthesis", new JsonObject { ["output_mod"] = "Output B" }),
			("texgen", new JsonObject { ["executable"] = "TexGen", ["output_mod"] = "Output A" }),
			("xlodgen", new JsonObject { ["executable"] = "xLODGen", ["output_mod"] = "Output A" }));
		// one more with a window of its own, switched off
		var root = (JsonObject)setup.Root.DeepClone();
		((JsonArray)root["steps"]!).Add(new JsonObject { ["step"] = "dyndolod", ["on"] = false, ["settings"] = new JsonObject { ["executable"] = "DynDOLOD" } });
		setup = Setup.FromJson(root, f.SetupFile);
		var tools = ToolSettings.ToSetUp(setup);
		Equal("xlodgen, texgen", string.Join(", ", tools.Select(t => t.Step)),
			"the switched-on steps with a window of their own, in the setup's order, each once (not the command-line one, not the switched-off one)");
		Equal(StepCatalog.Name("texgen"), tools[1].Name, "each under its tool's name");
		Check(tools.All(t => t.Learned == null), "none set up yet");
		var kept = Path.Combine(f.Root, "TexGen settings.ini");
		File.WriteAllText(kept, "a=1");
		ToolSettings.Keep(setup, "texgen", "TexGen", [kept]);
		tools = ToolSettings.ToSetUp(setup);
		Check(tools[0].Learned == null && tools[1].Learned is { Files.Count: 1 }, "a tool set up since is listed with what is kept of it, the other still without");

		// the box shows by itself once: before a first run, with something to set up, and not after the player went on from it
		Check(ToolSettings.OfferFirst(false, false, 2), "a setup that never ran and has tools to set up: offered");
		Check(!ToolSettings.OfferFirst(true, false, 2), "a setup that has run: not offered again");
		Check(!ToolSettings.OfferFirst(false, true, 2), "the player went on from the box before: not offered again");
		Check(!ToolSettings.OfferFirst(false, false, 0), "no tool with a window of its own: no box");
		Equal("tools.offered.my profile", ToolRunner.App.MainViewModel.ToolsOfferedKey(@"C:\Setups\My Profile.json"), "kept per setup, by its file's name");

		// what a row of the box says
		Check(ToolRunner.App.ToolsDialog.StateOf(null).StartsWith("not set up yet", StringComparison.Ordinal), "a tool not set up says so: " + ToolRunner.App.ToolsDialog.StateOf(null));
		Check(ToolRunner.App.ToolsDialog.StateOf(tools[1].Learned).StartsWith("set up ", StringComparison.Ordinal), "one set up says when: " + ToolRunner.App.ToolsDialog.StateOf(tools[1].Learned));
		Check(ToolRunner.App.ToolsDialog.AfterItsWindow("TexGen", tools[1].Learned, null).Contains("1 settings file(s) kept", StringComparison.Ordinal), "after its window: what was kept");
		// a tool closed with nothing changed is no fault: it is left as it is (the words are Learn's own, and the catalog's)
		var nothing = $"TexGen {ToolSettings.SavedNothing} - change a setting (or change one and back), save, close it, and learn again";
		Check(ToolRunner.App.ToolsDialog.AfterItsWindow("TexGen", null, nothing).StartsWith("left as it is", StringComparison.Ordinal), "a window closed with nothing saved leaves the tool as it is");
		Check(RunnerErrors.Find(nothing) != null, "and those words are still in the catalog");
		Equal("not set up: Mod Organizer has no executable called \"TexGen\"", ToolRunner.App.ToolsDialog.AfterItsWindow("TexGen", null, "Mod Organizer has no executable called \"TexGen\""),
			"any other reason is said as it is");
		// the mod a tool's window was started from, when it could not be switched off again: named on the tool's row, where the
		// player looks (the box shows no log)
		Equal("", ToolRunner.App.ToolsDialog.StillOn(null), "nothing is added when the mod went off again, or there was none");
		var left = ToolRunner.App.ToolsDialog.StillOn("TexGen in a mod");
		Check(left.Contains("\"TexGen in a mod\"", StringComparison.Ordinal) && left.Contains("untick it in Mod Organizer", StringComparison.Ordinal), "a mod left switched on is named, with what to do: " + left);
	}

	sealed class CrashForTest : Exception;

	// a crash = the runner process dying mid-step: the exception escapes the step, and nothing after it runs
	static void RunUntilCrash(FakeMo2 a_f)
	{
		var s = Setup.Load(a_f.SetupFile);
		var mo2 = Mo2Instance.Load(s.Mo2Exe, s.Mo2Instance);
		var runs = Runner.RunsDir(a_f.Data);
		var j = new Journal { Id = "crash", Folder = Path.Combine(runs, "0000 crash"), SetupFile = Path.GetFullPath(a_f.SetupFile), SetupName = "Test", Profile = "Test",
			Mo2Exe = s.Mo2Exe, Mo2Instance = s.Mo2Instance, BackupDir = Path.Combine(a_f.Backups, "crash"), Pid = Environment.ProcessId, Started = DateTime.Now,
			State = RunState.Running, Steps = [new StepRecord { Key = "landscapefixer", State = StepState.Running }] };
		Directory.CreateDirectory(j.Folder);
		var profile = new Profile(mo2, "Test", () => false);
		j.SaveSnapshot(profile.Snapshot());
		// as Runner.Run: the player's changes watched from here, the expectation kept in the run folder
		profile.OursFile = Path.Combine(j.Folder, Profile.WatchFileName);
		profile.WatchForOthers();
		profile.SetMods([], ["Output A", "Output B", "Needs B"]);
		j.Save();
		using var log = new RunLog(j.Folder);
		var ctx = new RunContext { Setup = s, Mo2 = mo2, Profile = profile, Control = new Mo2Control(mo2, "Test", log), Log = log, Journal = j,
			Cancel = CancellationToken.None, BackupFolder = j.BackupDir };
		StepRegistry.For("landscapefixer")!.Run(ctx, new StepSettings(s.StepSettings("landscapefixer")!));
	}

	static void T_OneRunAtATime()
	{
		using var f = new FakeMo2();
		using var hold = new ManualResetEventSlim();
		using var started = new ManualResetEventSlim();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			started.Set();
			hold.Wait(10000);
		}));
		f.WriteSetup(("landscapefixer", []));
		var first = Task.Run(() => Runner.Run(new RunRequest(f.SetupFile, f.Data)));
		started.Wait(10000);
		var second = Task.Run(() => Runner.Run(new RunRequest(f.SetupFile, f.Data))).Result;
		hold.Set();
		first.Wait();
		Check(second.Verdict.Contains("another run is going", StringComparison.Ordinal), "the second run is refused: " + second.Verdict);
		Equal(RunState.Finished, first.Result.State, "the first finishes");
	}

	static void T_GuidedStepWaitsForContinue()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			var t = Task.Run(() => {
				Thread.Sleep(800);
				Commands.Send(ctx.Journal.Folder, "continue");
			});
			ctx.Guide.Ask("landscapefixer", "do the thing", ctx.Cancel);
			t.Wait();
		}));
		f.WriteSetup(("landscapefixer", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "the guided step went on after Done: " + r.Verdict);
	}

	// A run that starts at a later step leaves the earlier steps' outputs on - the setup's
	// "keepEarlierOutputs", on unless it says off - and the whole run, its preview and its checks use the same list
	static void T_ARunFromALaterStepKeepsTheEarlierOutputs()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("synthesis", new JsonObject { ["output_mod"] = "Output A" }), ("xlodgen", new JsonObject { ["output_mod"] = "Output B" }));
		var st = ProfileOf(f).States();
		string Off(Setup a_setup, IReadOnlyList<Setup.StepEntry>? a_todo) =>
			string.Join("|", Runner.OutputsToTurnOff(a_setup, st, a_todo).Select(o => o.Mod).OrderBy(m => m, StringComparer.Ordinal));
		Equal("Output A|Output B", Off(setup, null), "a whole run: every output off");
		Equal("Output A|Output B", Off(setup, Runner.Plan(setup, null, null, null)), "a run from the first step: the same");
		var from = Runner.Plan(setup, null, "xlodgen", null);
		Equal("Output B", Off(setup, from), "a run from the second step leaves the first step's output on");
		Equal("Output A", Runner.EarlierOutputs(setup, from).Single(), "and names it");
		Equal("Output B", Off(setup, Runner.Plan(setup, "xlodgen", null, null)), "so does a run of that one step only");
		Equal("Output A|Output B", Off(setup, Runner.Plan(setup, null, null, "synthesis")), "a run that ends early still switches the later output off");
		Check(Runner.KeepsEarlierOutputs(setup), "on unless the setup says off");
		// the player's choice: off, every output goes off as in a whole run
		var root = (JsonObject)setup.Root.DeepClone();
		root[Runner.KeepEarlierSetting] = false;
		var offSetup = Setup.FromJson(root, f.SetupFile);
		Equal("Output A|Output B", Off(offSetup, Runner.Plan(offSetup, null, "xlodgen", null)), "set off: the earlier output goes off too");
		// an output a step at or after the start makes again is that step's, and goes off for it
		var shared = f.WriteSetup(("synthesis", new JsonObject { ["output_mod"] = "Output A" }), ("xlodgen", new JsonObject { ["output_mod"] = "Output A" }));
		Equal("Output A", Off(shared, Runner.Plan(shared, null, "xlodgen", null)), "an output the run makes again goes off for it");
		// a step this version does not know is in no run: its output is not kept on as an earlier one, and a later step that makes
		// the same output switches it off as in a whole run
		var unknown = f.WriteSetup(("easynpc", new JsonObject { ["output_mod"] = "Shared" }), ("synthesis", new JsonObject { ["output_mod"] = "Output A" }),
			("dyndolod", new JsonObject { ["output_mod"] = "Shared" }));
		Check(!Runner.EarlierOutputs(unknown, Runner.Plan(unknown, "synthesis", null, null)).Contains("Shared"), "an unknown step keeps nothing on");
		// an earlier output that a step of the run replaces goes off all the same
		var replacing = f.WriteSetup(("synthesis", new JsonObject { ["output_mod"] = "Output A" }), ("xlodgen", new JsonObject { ["output_mod"] = "Output B", ["replaces"] = new JsonArray("Output A") }));
		Equal("Output A|Output B", Off(replacing, Runner.Plan(replacing, null, "xlodgen", null)), "an earlier output the run's step replaces goes off for it");
		// ... the rival check counts the earlier output kept on as a rival, as the run leaves it
		Check(!Duplicates.Off(setup, st, from).Contains("Output A") && Duplicates.Off(setup, st).Contains("Output A"), "the rival check's off list is the run's own");
		// ... a step this version does not have is no kept step either, its old output left alone
		var old = Setup.FromJson(new JsonObject { ["steps"] = new JsonArray(
			new JsonObject { ["step"] = "easynpc", [OldOutput.Key] = true, ["settings"] = new JsonObject { ["output_mod"] = "Old NPC" } },
			new JsonObject { ["step"] = "synthesis", [OldOutput.Key] = true, ["settings"] = new JsonObject { ["output_mod"] = "Output A" } }) });
		Equal("synthesis", string.Join(",", old.KeptSteps.Select(s => s.Key)), "only a step this version has keeps its old output");
		// ... a run recorded by an older version that changed the plugin order still left something for Undo
		var legacy = Path.Combine(f.Data, "legacy run");
		Directory.CreateDirectory(legacy);
		File.WriteAllText(Path.Combine(legacy, "journal.json"), "{ \"OrderChanged\": true }");
		Check(RunUndo.LeftSomething(Journal.Load(legacy)), "an older run's changed order still counts for Undo");
		Directory.Delete(legacy, true);
		// the run itself: the earlier output stays on through the run, and the log says why
		StepRegistry.Replace(new FakeStep("xlodgen", (ctx, s) => {
			Check(ctx.Profile.IsOn("Output A") && !ctx.Profile.IsOn("Output B"), "while the later tool runs, the earlier output is on and its own is off");
			WriteOutput(ctx, "xlodgen", "Output B", "OutB.esp");
		}));
		f.WriteSetup(("synthesis", new JsonObject { ["output_mod"] = "Output A" }), ("xlodgen", new JsonObject { ["output_mod"] = "Output B" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data, null, "xlodgen"));
		Equal(RunState.Finished, r.State, "the run from the second step finished: " + r.Verdict);
		Check(File.ReadAllText(Path.Combine(r.Folder!, "log.txt")).Contains("left on, made by the steps before this run's first one", StringComparison.Ordinal), "and its log says what was left on");
	}

	// a file put back is as it was, its time too: in overwrite a new time would read as the step's own file
	static void T_AFilePutBackKeepsItsTime()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("acmos", []));
		var ctx = StepContext(f, "acmos");
		using var ctxLog = ctx.Log;
		var file = Path.Combine(f.Overwrite, "kept.ini");
		File.WriteAllText(file, "before");
		var then = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(file, then);
		StepKit.KeepFile(ctx, file);
		File.WriteAllText(file, "changed");
		Equal(0, StepKit.PutFilesBack(ctx, file).Count, "put back");
		Equal("before", File.ReadAllText(file), "its words");
		Equal(then, File.GetLastWriteTimeUtc(file), "and its time");
	}

	// a program anyone can be running counts as the tool only when it is Mod Organizer's; a script's own program is the command interpreter
	static void T_ASharedProgramCountsOnlyWhenItIsMo2s()
	{
		Check(Mo2Control.IsShared("javaw.exe") && Mo2Control.IsShared("CMD.EXE") && !Mo2Control.IsShared("TexGenx64.exe"), "which programs are anyone's");
		using var ping = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
		try {
			Equal(0, Mo2Control.FreshPid("PING.EXE", [], _ => false), "somebody else's is not the tool");
			Equal(ping.Id, Mo2Control.FreshPid("PING.EXE", [], id => id == ping.Id), "its own is");
		} finally {
			ping.Kill();
			ping.WaitForExit();
		}
		using var f = new FakeMo2();
		var ini = Path.Combine(f.Instance, "ModOrganizer.ini");
		// a Java tool's version is Java's, not the tool's: not known, and never read as up to date
		var java = Path.Combine(f.Root, "tools", "javaw.exe");
		Directory.CreateDirectory(Path.GetDirectoryName(java)!);
		File.WriteAllText(java, "");
		File.WriteAllText(ini, ToolFinder.WithExecutable(File.ReadAllText(ini), "Java Tool", java, ""));
		Equal("", ToolUpdates.Yours(Mo2Instance.Load(f.Exe, f.Instance), new StepSettings(new JsonObject { ["executable"] = "Java Tool" })).Version, "no version from Java");
	}

	// Root Builder's folder for this game and version decides; a mark left by another copy says nothing about this one
	static void T_RootBuilderIsAskedAboutThisGame()
	{
		Equal("CGamesSkyrim_SE", Mo2Instance.RootBuilderFolderName(@"C:\Games\Skyrim SE"), "named as Root Builder names it");
		Equal("1_6_1170_0", Mo2Instance.RootBuilderFolderName("1.6.1170.0"), "a version too");
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var data = Path.Combine(Path.GetDirectoryName(f.Exe)!, "plugins", "data", "RootBuilder");
		var other = Path.Combine(data, "another copy", "1_5_97_0");
		Directory.CreateDirectory(other);
		File.WriteAllText(Path.Combine(other, "BuildData.json"), "{}");
		Check(mo2.RootBuilderDeployed(), "no folder for this game yet: any mark counts");
		var mine = Path.Combine(data, Mo2Instance.RootBuilderFolderName(mo2.GameDir));
		Directory.CreateDirectory(mine);
		Check(!mo2.RootBuilderDeployed(), "its own folder holds none: another copy's mark says nothing");
		File.WriteAllText(Path.Combine(mine, "BuildData.json"), "{}");
		Check(mo2.RootBuilderDeployed(), "its own mark does");
	}

	// The languages shipped inside the program: each one reads, gives every text a translation (none left empty), keeps one access
	// key where the English has one and every key shortcut the English names, and is offered in the language list
	static void T_EveryShippedLanguageReadsWhole()
	{
		var shipped = ToolRunner.App.WindowWords.Shipped();
		foreach (var name in new[] { "Deutsch", "Español", "Français", "Italiano", "Polski", "Português (Brasil)", "Türkçe", "Русский", "Українська", "日本語", "简体中文", "한국어" }) {
			Check(shipped.Contains(name), "shipped: " + name);
		}
		var offered = ToolRunner.App.WindowWords.Available();
		Check(offered[0] == ToolRunner.App.WindowWords.English && shipped.All(offered.Contains), "English first, then every shipped language: " + string.Join(", ", offered));
		foreach (var name in shipped) {
			var words = ToolRunner.App.WindowWords.Read(ToolRunner.App.WindowWords.ShippedText(name)!, out var problem);
			Check(words != null, $"{name} reads: {problem}");
			Check(words!.Count >= 300, $"{name} holds the window's texts ({words.Count})");
			foreach (var (english, word) in words) {
				Check(!string.IsNullOrWhiteSpace(word), $"{name}: no empty translation for {english}");
				if (english.Count(c => c == '_') == 1) {
					Check(word.Count(c => c == '_') == 1, $"{name}: one access key in {word}");
				}
				foreach (var key in System.Text.RegularExpressions.Regex.Matches(english, @"Ctrl\+(?:Enter|[A-Z,])|\bF\d\b").Select(m => m.Value)) {
					Check(word.Contains(key, StringComparison.Ordinal), $"{name}: {key} kept in {word}");
				}
			}
		}
	}

	// Every text the window's code hands to WindowWords - Word("..."), Format("...", values), F($"...") - has its words in every
	// shipped language. Read off Runner.App's own source: a text added without its words fails here, not in front of a player
	static void T_EveryTextTheCodeShowsIsInEveryLanguage()
	{
		var app = Path.Combine(Path.GetDirectoryName(SourceDir())!, "Runner.App");
		if (!Directory.Exists(app)) {
			Console.WriteLine($"  (skipped: the source is not at {app})");
			return;
		}
		// (the scan itself, on a made-up file: a call inside a comment is no text the window shows, and an escape is read as
		// the compiler reads it)
		var fixture = SourceWindowWords("// WindowWords.Word(\"Draft label\")\n/* WindowWords.Word(\"Also a draft\") */\nvar a = WindowWords.Word(\"\\x0041\");\n" +
			"var b = WindowWords.Word(\"\\x41g\\U0001F600\\a\");\n", "Fixture.cs");
		Equal("A | Ag\U0001F600\a", string.Join(" | ", fixture.Keys), "only the real calls, their escapes read as the compiler reads them");
		var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var file in Directory.GetFiles(app, "*.cs")) {
			foreach (var (key, where) in SourceWindowWords(File.ReadAllText(file), Path.GetFileName(file))) {
				keys.TryAdd(key, where);
			}
		}
		Check(keys.Count > 250, "the texts were found in the source: " + keys.Count);
		foreach (var language in ToolRunner.App.WindowWords.Shipped()) {
			var words = ToolRunner.App.WindowWords.Read(ToolRunner.App.WindowWords.ShippedText(language)!, out _)!;
			var missing = keys.Where(k => !words.ContainsKey(k.Key)).Select(k => $"\"{k.Key}\" ({k.Value})").ToList();
			Check(missing.Count == 0, $"{language} lacks {missing.Count} text(s) the code shows: {string.Join(" | ", missing.Take(4))}");
		}
	}

	// The texts one C# file hands to WindowWords - Word("..."), Format("...", values), F($"...") - each with where it stands.
	// A call inside a comment hands nothing over, and is left out
	static SortedDictionary<string, string> SourceWindowWords(string a_code, string a_name)
	{
		var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);
		var comment = SourceCommentMask(a_code);
		// (in its own file the calls stand without the class's name: the ones its properties make, never a method's own first line)
		var own = a_name == "WindowWords.cs";
		foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(a_code, own ? @"(?<![A-Za-z_.])(Word|Format)\(" : @"WindowWords\.(Word|Format|F)\(")) {
			if (comment[m.Index] || own && a_code[Math.Max(0, m.Index - 30)..m.Index].TrimEnd().EndsWith("string", StringComparison.Ordinal)) {
				continue;
			}
			var start = m.Index + m.Length;
			var where = $"{a_name}:{a_code.AsSpan(0, start).Count('\n') + 1}";
			if (m.Groups[1].Value == "F") {
				var at = start;
				while (char.IsWhiteSpace(a_code[at])) {
					++at;
				}
				Check(a_code[at] == '$' && a_code[at + 1] == '"', $"{where}: F( takes one $\"...\" text");
				keys.TryAdd(SourceInterpolated(a_code, at).Format, where);
			} else {
				var end = SourceSkip(a_code, start, m.Groups[1].Value == "Format" ? ")," : ")");
				foreach (var text in SourceLiterals(a_code, start, end).Where(x => x.Trim() != "")) {
					keys.TryAdd(text, where);
				}
			}
		}
		return keys;
	}

	// Which characters of a C# file stand inside a comment (// to the line's end, /* to */). Strings and characters are
	// stepped over - a // inside a text is no comment - regular, verbatim (@"", with "" for a quote) and interpolated alike
	static bool[] SourceCommentMask(string a_s)
	{
		var mask = new bool[a_s.Length + 1];
		for (var i = 0; i < a_s.Length; ++i) {
			var c = a_s[i];
			var next = i + 1 < a_s.Length ? a_s[i + 1] : '\0';
			if (c == '/' && next == '/') {
				var end = a_s.IndexOf('\n', i);
				end = end < 0 ? a_s.Length : end;
				Array.Fill(mask, true, i, end - i);
				i = end;
			} else if (c == '/' && next == '*') {
				var end = a_s.IndexOf("*/", i + 2, StringComparison.Ordinal);
				end = end < 0 ? a_s.Length : end + 2;
				Array.Fill(mask, true, i, end - i);
				i = end - 1;
			} else if (c == '@' && next == '"' || c is '$' or '@' && next is '$' or '@' && i + 2 < a_s.Length && a_s[i + 2] == '"') {
				// a verbatim text: it ends at a lone quote
				var j = a_s.IndexOf('"', i) + 1;
				while (j < a_s.Length && !(a_s[j] == '"' && (j + 1 >= a_s.Length || a_s[j + 1] != '"'))) {
					j += a_s[j] == '"' ? 2 : 1;
				}
				i = j;
			} else if (c == '"') {
				i = SourceString(a_s, i).After - 1;
			} else if (c == '$' && next == '"') {
				i = SourceInterpolated(a_s, i).After - 1;
			} else if (c == '\'') {
				// (an escaped quote or a \u escape closes after its escape: the quote looked for begins past the backslash's own character)
				i += next == '\\' ? (a_s.IndexOf('\'', i + 3) - i) : 2;
			}
		}
		return mask;
	}

	// One escape of a C# text at a_s[a_i] (a backslash), read as the compiler reads it: what it stands for, and how many
	// characters it takes. \x takes one to four hex digits, as many as follow; \U eight, one character past the first plane
	static (string Value, int Length) SourceEscape(string a_s, int a_i)
	{
		static bool Hex(char a_c) => char.IsAsciiHexDigit(a_c);
		int Digits(int a_most) {
			var n = 0;
			while (n < a_most && a_i + 2 + n < a_s.Length && Hex(a_s[a_i + 2 + n])) {
				++n;
			}
			return n;
		}
		int Value(int a_n) => int.Parse(a_s.AsSpan(a_i + 2, a_n), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
		var n = a_s[a_i + 1];
		switch (n) {
			case 'u':
				return (((char)Value(4)).ToString(), 6);
			case 'x': {
				var d = Digits(4);
				return (((char)Value(d)).ToString(), 2 + d);
			}
			case 'U':
				return (char.ConvertFromUtf32(Value(8)), 10);
			default:
				return ((n switch {
					'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', 'e' => '\u001b',
					_ => n,
				}).ToString(), 2);
		}
	}

	// --- C# source read for its texts: a string, an interpolated string, and the code between them
	// a_s[a_i] opens a plain string: what it holds, and the place after its closing quote
	static (string Value, int After) SourceString(string a_s, int a_i)
	{
		var sb = new StringBuilder();
		var i = a_i + 1;
		while (a_s[i] != '"') {
			if (a_s[i] != '\\') {
				sb.Append(a_s[i++]);
				continue;
			}
			var (value, length) = SourceEscape(a_s, i);
			sb.Append(value);
			i += length;
		}
		return (sb.ToString(), i + 1);
	}

	// from a_i on, code is stepped over - strings, characters, comments, and brackets with all they hold - to the first of
	// a_closers that stands outside every one of them: its place
	static int SourceSkip(string a_s, int a_i, string a_closers)
	{
		var depth = 0;
		for (var i = a_i; ; ++i) {
			var c = a_s[i];
			if (depth == 0 && a_closers.Contains(c)) {
				return i;
			}
			if (c is '(' or '[' or '{') {
				++depth;
			} else if (c is ')' or ']' or '}') {
				--depth;
			} else if (c == '"') {
				i = SourceString(a_s, i).After - 1;
			} else if (c == '$' && a_s[i + 1] == '"') {
				i = SourceInterpolated(a_s, i).After - 1;
			} else if (c == '\'') {
				i += a_s[i + 1] == '\\' ? 3 : 2;
			} else if (c == '/' && a_s[i + 1] == '/') {
				i = a_s.IndexOf('\n', i);
			}
		}
	}

	// a_s[a_i] is the $ of $"...": the text as a FormattableString gives it ({0}, {1:00} where the values stand), and the
	// place after its closing quote
	static (string Format, int After) SourceInterpolated(string a_s, int a_i)
	{
		var sb = new StringBuilder();
		var holes = 0;
		var i = a_i + 2;
		while (a_s[i] != '"') {
			var c = a_s[i];
			if (c == '\\') {
				var (value, length) = SourceEscape(a_s, i);
				sb.Append(value);
				i += length;
			} else if ((c == '{' || c == '}') && a_s[i + 1] == c) {
				sb.Append(c).Append(c);
				i += 2;
			} else if (c == '{') {
				// (a value ends at its bracket, or at the comma or colon that begins how it is laid out)
				var end = SourceSkip(a_s, i + 1, "},:");
				var spec = "";
				if (a_s[end] != '}') {
					var close = SourceSkip(a_s, end, "}");
					spec = a_s[end..close];
					end = close;
				}
				sb.Append('{').Append(holes.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(spec).Append('}');
				++holes;
				i = end + 1;
			} else {
				sb.Append(c);
				++i;
			}
		}
		return (sb.ToString(), i + 1);
	}

	// the texts in the expression a_s[a_i..a_stop]: literals joined by + are one text, the two sides of a choice are two, and a
	// literal a comparison stands right before is none (it is compared with, not shown)
	static List<string> SourceLiterals(string a_s, int a_i, int a_stop)
	{
		var found = new List<string>();
		string? cur = null;
		var joined = false;
		var compared = false;
		for (var i = a_i; i < a_stop; ++i) {
			var c = a_s[i];
			if (c == '"') {
				var (value, after) = SourceString(a_s, i);
				if (compared) {
					compared = false;
				} else if (cur != null && joined) {
					cur += value;
				} else {
					if (cur != null) {
						found.Add(cur);
					}
					cur = value;
				}
				joined = false;
				i = after - 1;
			} else if (c == '$' && a_s[i + 1] == '"') {
				Check(false, "a text with values in it goes through F($\"...\"), never Word or Format: " + a_s[i..Math.Min(a_s.Length, i + 60)]);
			} else if (c == '/' && a_s[i + 1] == '/') {
				i = a_s.IndexOf('\n', i);
			} else if (c == '+') {
				joined = true;
			} else if (c is '=' or '!' && a_s[i + 1] == '=') {
				var next = i + 2;
				while (char.IsWhiteSpace(a_s[next])) {
					++next;
				}
				compared = a_s[next] == '"';
				++i;
			} else if (!char.IsWhiteSpace(c)) {
				if (c is '?' or ':' or ',') {
					joined = false;
				}
				if ((char.IsLetter(c) || c is '_' or '(') && cur != null && !joined) {
					found.Add(cur);
					cur = null;
				}
			}
		}
		if (cur != null) {
			found.Add(cur);
		}
		return found;
	}

	// a part of a window drawn again is walked again: a translation that is another text's English, with a translation of its
	// own, would then be translated twice. And every value a text takes ({0}, {1:0.0}) is in its translation
	static void T_NoTranslationIsAnotherTextsEnglish()
	{
		foreach (var name in ToolRunner.App.WindowWords.Shipped()) {
			var words = ToolRunner.App.WindowWords.Read(ToolRunner.App.WindowWords.ShippedText(name)!, out _)!;
			var twice = words.Where(p => p.Value != p.Key && words.TryGetValue(p.Value, out var again) && again != p.Value).Select(p => p.Key).ToList();
			Check(twice.Count == 0, $"{name}: nothing is translated twice ({string.Join(" | ", twice.Take(5))})");
			foreach (var (english, word) in words) {
				var places = System.Text.RegularExpressions.Regex.Matches(english, @"\{\d+(?:[,:][^}]*)?\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToList();
				var kept = System.Text.RegularExpressions.Regex.Matches(word, @"\{\d+(?:[,:][^}]*)?\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToList();
				Check(places.SequenceEqual(kept), $"{name}: every value of \"{english}\" has its place in \"{word}\"");
			}
		}
	}

	// the languages packed into the program are the language files, every one, byte for byte, each found by its name (names
	// with letters of other alphabets too) - and nothing else is in the pack
	static void T_ThePackedLanguagesAreTheLanguageFiles()
	{
		var folder = Path.Combine(Path.GetDirectoryName(SourceDir())!, "Runner.App", "Languages");
		if (!Directory.Exists(folder)) {
			Console.WriteLine($"  (skipped: the source is not at {folder})");
			return;
		}
		var files = Directory.GetFiles(folder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal).ToList();
		Check(files.Count >= 12, "the language files were found: " + files.Count);
		Equal(string.Join("|", files), string.Join("|", ToolRunner.App.WindowWords.Shipped()), "the pack holds every language file and nothing else");
		foreach (var name in files) {
			Equal(File.ReadAllText(Path.Combine(folder, name + ".json")), ToolRunner.App.WindowWords.ShippedText(name), name + ": the packed text is the file's");
		}
		Equal<string?>(null, ToolRunner.App.WindowWords.ShippedText("No Such Language"), "a language that is not there");
	}

	// a folder counted, sized and dated without an object for each file comes to what the walk over every file came to
	static void T_AFolderIsCountedAsEveryFileInIt()
	{
		using var f = new FakeMo2();
		var dir = Path.Combine(f.Root, "counted");
		Directory.CreateDirectory(Path.Combine(dir, "sub", "deeper"));
		Directory.CreateDirectory(Path.Combine(dir, "empty"));
		var written = new[] { ("a.txt", 3, 2026), ("sub/b.dds", 5, 2025), ("sub/deeper/c.nif", 7, 2024), ("sub/.hidden", 11, 2023) };
		foreach (var (name, size, year) in written) {
			var file = Path.Combine(dir, name.Replace('/', '\\'));
			File.WriteAllBytes(file, new byte[size]);
			File.SetLastWriteTimeUtc(file, new DateTime(year, 3, 4, 5, 6, 7, DateTimeKind.Utc));
		}
		File.SetAttributes(Path.Combine(dir, "sub", ".hidden"), FileAttributes.Hidden | FileAttributes.ReadOnly);
		try {
			var every = new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList();
			Equal(every.Count, Files.CountFiles(dir), "as many files as the walk over every file finds, a hidden one too");
			Equal(every.Sum(x => x.Length), Files.SizeOf(dir), "their size");
			Equal(every.Max(x => x.LastWriteTimeUtc), Files.NewestWrite(dir), "the newest time");
			Equal(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc), Files.NewestWrite(dir), "... which is the newest file's, in UTC");
			Equal(4, Files.CountFiles(dir), "four files");
			Equal(26L, Files.SizeOf(dir), "26 bytes");
			var gone = Path.Combine(dir, "not there");
			Check(Files.CountFiles(gone) == 0 && Files.SizeOf(gone) == 0 && Files.NewestWrite(gone) == DateTime.MinValue, "a folder that is not there");
		} finally {
			File.SetAttributes(Path.Combine(dir, "sub", ".hidden"), FileAttributes.Normal);
		}
	}

	// a shader-log line that counts what is left and also says a compile ended is kept as both: a later count never takes
	// away that the compile ended
	static void T_AShaderLogLineWithACountAndAMarkIsKeptAsBoth()
	{
		var d = new ShaderStep.LogDigest();
		// the line alone first: its count is kept with it, not only its mark
		d.Take("[12:00:00.000] [info] [ShaderTiming] Shader compilation completed, remaining=7\n");
		Check(d.Text.Contains("Shader compilation completed", StringComparison.Ordinal), "the line that says the compile ended is kept: " + d.Text);
		Equal<int?>(7, InGame.RemainingIn(d.Text), "and so is its count");
		// a later count takes the count's place, never the mark's
		d.Take("[12:00:01.000] [info] [ShaderTiming] slow shader: 10 ms, remaining=3\n");
		Check(d.Text.Contains("Shader compilation completed", StringComparison.Ordinal), "the line that says the compile ended stays: " + d.Text);
		Equal<int?>(3, InGame.RemainingIn(d.Text), "the newest count is the one asked");
		Check(d.Text.EndsWith("slow shader: 10 ms, remaining=3", StringComparison.Ordinal), "and stands last");
	}

	// what a screen reader says of a control - its name, and the help text after it - goes into the window's language with
	// the rest of its words; a help text a binding fills is the binding's (a step row's own, already in the language)
	static void T_AScreenReadersWordsAreInTheWindowsLanguage()
	{
		Exception? failed = null;
		var t = new Thread(() => {
			try {
				ToolRunner.App.WindowWords.Use(new Dictionary<string, string> {
					["Restore"] = "Wiederherstellen", ["Puts it back"] = "Stellt es wieder her", ["Restore the run"] = "Den Lauf wiederherstellen", ["Double-click"] = "Doppelklick",
				});
				var button = new System.Windows.Controls.Button { Content = "Restore" };
				System.Windows.Automation.AutomationProperties.SetName(button, "Restore the run");
				System.Windows.Automation.AutomationProperties.SetHelpText(button, "Puts it back");
				var bound = new System.Windows.Controls.Button { Content = "Restore", DataContext = new { Help = "Puts it back" } };
				System.Windows.Data.BindingOperations.SetBinding(bound, System.Windows.Automation.AutomationProperties.HelpTextProperty, new System.Windows.Data.Binding("Help"));
				var menu = new System.Windows.Controls.ContextMenu();
				var entry = new System.Windows.Controls.MenuItem { Header = "Restore", InputGestureText = "Double-click" };
				var key = new System.Windows.Controls.MenuItem { Header = "Restore", InputGestureText = "Ctrl+N" };
				menu.Items.Add(entry);
				menu.Items.Add(key);
				button.ContextMenu = menu;
				var panel = new System.Windows.Controls.StackPanel();
				panel.Children.Add(button);
				panel.Children.Add(bound);
				ToolRunner.App.WindowWords.Tree(panel);
				Check(entry.Header as string == "Wiederherstellen" && entry.InputGestureText == "Doppelklick", "a menu entry's words, and the words at its right");
				Equal("Ctrl+N", key.InputGestureText, "a key's name stays as Windows writes it");
				Equal("Wiederherstellen", button.Content as string, "its words");
				Equal("Den Lauf wiederherstellen", System.Windows.Automation.AutomationProperties.GetName(button), "its name for a screen reader");
				Equal("Stellt es wieder her", System.Windows.Automation.AutomationProperties.GetHelpText(button), "and the help text after it");
				Check(System.Windows.Data.BindingOperations.IsDataBound(bound, System.Windows.Automation.AutomationProperties.HelpTextProperty),
					"a help text a binding fills keeps its binding");
			} catch (Exception e) when (e is not OutOfMemoryException) {
				failed = e;
			} finally {
				ToolRunner.App.WindowWords.Use(new Dictionary<string, string>());
			}
		});
		t.SetApartmentState(ApartmentState.STA);
		t.Start();
		t.Join();
		if (failed != null) {
			throw new InvalidOperationException("the walk over a made-up window failed: " + failed.Message, failed);
		}
	}

	// a text with values in it is looked up by its form ("Use {0}'s old output?"), the values put in as they are
	static void T_ATextWithValuesIsLookedUpByItsForm()
	{
		var tool = "DynDOLOD";
		var at = 3;
		var of = 13;
		Check(ToolRunner.App.WindowWords.F($"Use {tool}'s old output?") == "Use DynDOLOD's old output?", "with no language in use it reads as written");
		try {
			ToolRunner.App.WindowWords.Use(new Dictionary<string, string> {
				["Use {0}'s old output?"] = "Alte Ausgabe von {0} verwenden?", ["step {0} of {1}"] = "Schritt {0} von {1}", ["broken {0}"] = "kaputt {0} {1}",
			});
			Check(ToolRunner.App.WindowWords.F($"Use {tool}'s old output?") == "Alte Ausgabe von DynDOLOD verwenden?", "the language's form of it, the value as it is");
			Check(ToolRunner.App.WindowWords.F($"step {at} of {of}") == "Schritt 3 von 13", "two values");
			Check(ToolRunner.App.WindowWords.F($"broken {tool}") == "broken DynDOLOD", "a translation whose places do not fit falls back to the English");
			Check(ToolRunner.App.WindowWords.F($"not in the file: {tool}") == "not in the file: DynDOLOD", "a text the language lacks stays English");
		} finally {
			ToolRunner.App.WindowWords.Use(new Dictionary<string, string>());
		}
	}

	static void T_TimeBetweenTwoClockReadings()
	{
		// (two instants three hours apart, read as the PC's own clock shows them - in many places a daylight-saving change lies between)
		var from = new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc).ToLocalTime();
		var to = new DateTime(2026, 3, 29, 3, 30, 0, DateTimeKind.Utc).ToLocalTime();
		Check(Times.Between(from, to) == TimeSpan.FromHours(3), "three hours are three hours, whatever the clock did between them");
		Check(Times.WholeMinutes(TimeSpan.FromMinutes(59.6)) == 60 && Times.WholeMinutes(TimeSpan.FromMinutes(-5)) == 0, "whole minutes, never below nothing");
		Check(Times.HoursAndMinutes(TimeSpan.FromMinutes(59.6)) == "1 h 00 min", "59.6 minutes read 1 h 00 min: " + Times.HoursAndMinutes(TimeSpan.FromMinutes(59.6)));
		Check(Times.HoursAndMinutes(TimeSpan.FromMinutes(44.4)) == "44 min" && Times.HoursAndMinutes(TimeSpan.FromMinutes(125)) == "2 h 05 min", "minutes, and hours with minutes");
		Check(RunSummary.Span(TimeSpan.FromMinutes(119.6)) == "2 h 00 min", "the summary rounds the same way: " + RunSummary.Span(TimeSpan.FromMinutes(119.6)));
		Check(ToolRunner.App.MainViewModel.Short(TimeSpan.FromMinutes(59.6)) == "1h 00m", "the window's short form: " + ToolRunner.App.MainViewModel.Short(TimeSpan.FromMinutes(59.6)));
		Check(ToolRunner.App.MainViewModel.Short(TimeSpan.FromSeconds(-300)) == "0s", "a time below nothing shows as none");
		Check(ToolRunner.App.MainViewModel.Long(TimeSpan.FromMinutes(59.6)) == "1 h 00 min", "the window's long form");
	}

	static void T_NamesWindowsKeepsForDevices()
	{
		foreach (var name in new[] { "CON", "nul", "COM1", "LPT9", "NUL.txt", "con.json" }) {
			Check(Setup.IsDeviceName(name) && !Setup.PlainName(name), name + " is a device's name, no folder's");
		}
		foreach (var name in new[] { "Console", "COM10", "Nullable", "My setup", "DynDOLOD Output - My Profile" }) {
			Check(!Setup.IsDeviceName(name) && Setup.PlainName(name), name + " is a plain name");
		}
		Check(SetupFiles.BadName(Path.GetTempPath(), "AUX") is { } why && why.Contains("Windows keeps", StringComparison.Ordinal), "a setup cannot be called AUX");
	}

	static void T_AThemeColorIsHexDigitsOrAName()
	{
		foreach (var good in new[] { "#FFF", "#1F6FD1", "#660B1A2E", "#abcd", "Red", " transparent " }) {
			Check(ToolRunner.App.Theme.PlainColor(good), good + " is a color");
		}
		foreach (var bad in new[] { "", "#12", "#GGGGGG", "ContextColor file:///C:/x.icc 1,0,0", "sc#1,0,0", "#FF FF FF", "Red;Blue" }) {
			Check(!ToolRunner.App.Theme.PlainColor(bad), "\"" + bad + "\" is refused");
		}
	}

	// A theme's picture is kept no larger than it is ever shown: a large one is made small as it is read, a small one is taken
	// as it is, and its file is let go either way (a theme's folder must stay free to move or remove)
	static void T_AThemesPictureIsKeptSmall()
	{
		// (on a single-threaded apartment of its own, as the window reads a theme's picture and as every other test here that
		// makes WPF objects runs)
		Exception? failed = null;
		var t = new Thread(() => {
			try {
				ThemesPictureIsKeptSmall();
			} catch (Exception e) when (e is not OutOfMemoryException) {
				failed = e;
			}
		});
		t.SetApartmentState(ApartmentState.STA);
		t.Start();
		t.Join();
		if (failed != null) {
			throw new InvalidOperationException("a theme's picture: " + failed.Message, failed);
		}
	}

	static void ThemesPictureIsKeptSmall()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "icon " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		try {
			string Picture(string a_name, int a_wide, int a_high)
			{
				var file = Path.Combine(dir, a_name);
				var made = System.Windows.Media.Imaging.BitmapSource.Create(a_wide, a_high, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[a_wide * a_high * 4], a_wide * 4);
				var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
				png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(made));
				using var to = File.Create(file);
				png.Save(to);
				return file;
			}
			var wide = ToolRunner.App.Theme.IconFrom(Picture("wide.png", 1200, 600));
			Equal((256, 128), (wide.PixelWidth, wide.PixelHeight), "a wide picture is read at 256 across, its shape kept");
			var tall = ToolRunner.App.Theme.IconFrom(Picture("tall.png", 300, 900));
			Check(tall.PixelHeight == 256 && tall.PixelWidth is >= 85 and <= 86, $"a tall one at 256 high: {tall.PixelWidth} x {tall.PixelHeight}");
			var small = ToolRunner.App.Theme.IconFrom(Picture("small.png", 64, 64));
			Equal((64, 64), (small.PixelWidth, small.PixelHeight), "a small one is taken as it is");
			Check(wide.IsFrozen && tall.IsFrozen && small.IsFrozen, "each can be used from any thread");
			foreach (var f in Directory.GetFiles(dir)) {
				File.Delete(f);
			}
			Check(Directory.GetFiles(dir).Length == 0, "no picture file is held open after it was read");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	static void T_SmallTextRulesOfTheWindow()
	{
		Check(ToolRunner.App.PanelWindow.Plain("HISTORY") == "History" && ToolRunner.App.PanelWindow.Plain("MO2 CHANGELOG") == "MO2 Changelog", "a heading as a window title");
		Check(ToolRunner.App.SettingsPanel.TypedPath("  \"C:\\Tools\\x.exe\"  ") == "C:\\Tools\\x.exe", "a path pasted with Explorer's quotes loses them");
		Check(ToolRunner.App.SettingsPanel.TypedPath(" C:\\a b ") == "C:\\a b" && ToolRunner.App.SettingsPanel.TypedPath("\"") == "\"", "a path without them is only trimmed");
		Check(ToolRunner.App.Cli.Handles(["frobnicate"]) && !ToolRunner.App.Cli.Handles(["--snapshot"]) && !ToolRunner.App.Cli.Handles([]), "a word that is no command is the command line's to refuse, not the window's to open on");
	}

	// what the window shows only once it is used - the step editor's labels, the settings choices not picked, the command box's
	// entries - is in every shipped language as well, not only what a picture of the window showed
	static void T_EveryShippedLanguageHasTheSettingsTexts()
	{
		string[] more = [.. ToolFinder.WhereTexts, .. LodGuide.Words, .. ToolUpdates.SummaryTexts, .. StepCatalog.All.Select(s => s.About), StepCatalog.Info(AddOns.KeyPrefix + "x").About, StepCatalog.RunsAfter,
			.. StepCatalog.MustPrecede.Select(r => r.Why).Distinct(), "LOD output mods switched off before the first step (one per line)", "Plugin places after a run (each: the plugin, then last_master, after:<plugin> or end)", "A plugin's place when a run first ticks it (the plugin: the plugin it goes below)",
			"{0} (default {1})", "Show setup: {0}", "Settings of {0}", "Run only {0}", "Find in the settings: \"{0}\"", "Stop the test", "Most recent save (any character)",
			"leave it to me (unanswered, the step stops)", "every run", "never", "skip it during the run (it asks in my own game)",
			"new cache at every game start", "leave it alone - warn me only", "off", "on", "none", "the step's own", "Data folder (setups, runs, backups): {0}", ToolRunner.App.MainWindow.FooterText, "Data folder: {0}"];
		var must = ToolRunner.App.SettingsPanel.StepLabels.Concat(more).ToList();
		Check(must.Count >= 55, $"the texts looked for ({must.Count})");
		foreach (var name in ToolRunner.App.WindowWords.Shipped()) {
			var words = ToolRunner.App.WindowWords.Read(ToolRunner.App.WindowWords.ShippedText(name)!, out _)!;
			var missing = must.Where(m => !words.ContainsKey(m)).ToList();
			Check(missing.Count == 0, $"{name} lacks {missing.Count}: {string.Join(" | ", missing.Take(5))}");
		}
		// a text with a name put into it: the language's own form, and English when a translation's places do not fit
		try {
			ToolRunner.App.WindowWords.Use(new Dictionary<string, string> { ["Run only {0}"] = "Nur {0} ausführen" });
			Equal("Nur xLODGen ausführen", ToolRunner.App.WindowWords.Format("Run only {0}", "xLODGen"), "the language's own form");
			ToolRunner.App.WindowWords.Use(new Dictionary<string, string> { ["Run only {0}"] = "Nur {1} ausführen" });
			Equal("Run only xLODGen", ToolRunner.App.WindowWords.Format("Run only {0}", "xLODGen"), "a translation whose places do not fit: English");
		} finally {
			ToolRunner.App.WindowWords.Use(new Dictionary<string, string>());
		}
	}

	// "Make a language file" lists every text the program can show, also the ones no window holds at that moment
	static void T_ALanguageFileListsEveryText()
	{
		var name = "a test " + Guid.NewGuid().ToString("N")[..8];
		var mine = Path.Combine(ToolRunner.App.WindowWords.Folder, name + ".json");
		try {
			Exception? failed = null;
			var t = new Thread(() => {
				try {
					ToolRunner.App.WindowWords.WriteTemplate(new System.Windows.Controls.StackPanel(), name);
				} catch (Exception e) {
					failed = e;
				}
			});
			t.SetApartmentState(ApartmentState.STA);
			t.Start();
			t.Join();
			if (failed != null) {
				throw failed;
			}
			var made = ToolRunner.App.WindowWords.Read(File.ReadAllText(mine), out var problem) ?? [];
			Check(problem == "", "the file made reads: " + problem);
			var shipped = ToolRunner.App.WindowWords.Read(ToolRunner.App.WindowWords.ShippedText("Deutsch")!, out _)!;
			var lacking = shipped.Keys.Where(k => !made.ContainsKey(k)).ToList();
			Check(lacking.Count == 0, $"the file made lacks {lacking.Count} of the program's texts: {string.Join(" | ", lacking.Take(3))}");
			Check(made.Count > 0 && made.Values.All(v => v == ""), "every text waits for its translation");
			Equal("Run xLODGen", new ToolRunner.App.StepRow { Key = "xlodgen", Name = "xLODGen", About = "" }.TickName, "a step's tick, named for a screen reader");
		} finally {
			File.Delete(mine);
		}
	}

	// a language file: a value left empty stays English, a name is only a name, and a file that does not read is not replaced
	static void T_ALanguageFileCannotHarmTheWindow()
	{
		ToolRunner.App.WindowWords.Use(new Dictionary<string, string> { ["Start"] = "Starten", ["Empty"] = null!, ["Blank"] = "  " });
		ToolRunner.App.WindowWords.Use(new Dictionary<string, string>());
		Check(ToolRunner.App.WindowWords.GoodName("Deutsch") && ToolRunner.App.WindowWords.GoodName("My language"), "a name");
		foreach (var bad in new[] { @"..\outside", "a/b", @"C:\x", "..", ".", " " }) {
			Check(!ToolRunner.App.WindowWords.GoodName(bad), "no language's name: " + bad);
		}
		var name = "a test " + Guid.NewGuid().ToString("N")[..8];
		var mine = Path.Combine(ToolRunner.App.WindowWords.Folder, name + ".json");
		Directory.CreateDirectory(ToolRunner.App.WindowWords.Folder);
		File.WriteAllText(mine, "{ broken");
		try {
			Exception? failed = null;
			var t = new Thread(() => {
				try {
					Throws<InvalidDataException>(() => ToolRunner.App.WindowWords.WriteTemplate(new System.Windows.Controls.StackPanel(), name), "a file that does not read stops it");
				} catch (Exception e) {
					failed = e;
				}
			});
			t.SetApartmentState(ApartmentState.STA);
			t.Start();
			t.Join();
			if (failed != null) {
				throw failed;
			}
			Equal("{ broken", File.ReadAllText(mine), "a translator's file that does not read is left as it is");
		} finally {
			File.Delete(mine);
		}
	}

	// a command whose program path is quoted (a path with spaces) runs as written - cmd's own quoting, not Windows' escaping
	// (a real run's after-run command failed with '\"C:\...python.exe\"' is not recognized)
	static void T_CommandWithAQuotedPathRuns()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => { }));
		var s = f.WriteSetup(("landscapefixer", []));
		var flag = Path.Combine(f.Root, "after ran.txt");
		var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
		s.Root["beforeRun"] = new JsonArray($"\"{cmd}\" /c exit 0");
		s.Root["afterRun"] = new JsonArray($"\"{cmd}\" /c echo ok> \"{flag}\"");
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		Check(File.Exists(flag), "the after-run command with quoted paths ran");
	}

	// a tool driven through its window whose window never changes is idle, whatever CPU its window uses drawing itself - the
	// step fails naming what the window shows (Synthesis sat on its main screen 4 min, its window using CPU)
	static void T_AWindowThatNeverChangesIsIdle()
	{
		using var f = new FakeMo2();
		Waits.IdleForTests = TimeSpan.FromSeconds(8);
		try {
			StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
				using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
				try {
					Waits.Tool(ctx, "landscapefixer", p, TimeSpan.FromMinutes(2), () => Waits.Poll.Wait, a_progress: () => "SYNTHESIS  Run");
				} finally {
					p.Kill();
				}
			}));
			f.WriteSetup(("landscapefixer", []));
			var sw = Stopwatch.StartNew();
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Equal(RunState.Failed, r.State, r.Verdict);
			Check(r.Verdict.Contains("sat idle", StringComparison.Ordinal) && r.Verdict.Contains("SYNTHESIS  Run", StringComparison.Ordinal), "named, with the window's text: " + r.Verdict);
			Check(sw.Elapsed < TimeSpan.FromSeconds(40), "within the idle limit, not the deadline: " + sw.Elapsed);
		} finally {
			Waits.IdleForTests = null;
		}
	}

	// a Synthesis patcher that failed is named off Synthesis's own log once its run ends (a patcher
	// could not write a compressed record, no Synthesis.esp, and the step only said "sat idle")
	static void T_SynthesisPatcherFailureIsNamed()
	{
		const string head = """
			2026-10-27 16:04:36.640 -04:00 [INF] [T16] ================= Starting Patcher Example Patcher Run =================
			2026-10-27 16:04:44.387 -04:00 [INF] [T34] Writing to output: C:\Temp\Synthesis.esp
			2026-10-27 16:04:45.899 -04:00 [ERR] [T35] System.NotImplementedException: Writing with compression enabled is not currently supported.
			2026-10-27 16:04:45.907 -04:00 [ERR] [T35] Error while running:
			""";
		Equal<string?>(null, SynthesisStep.Failure(head), "still running: not judged yet");
		var why = SynthesisStep.Failure(head + "2026-10-27 16:04:45.911 -04:00 [INF] [T2] Finished patcher run\n");
		Check(why != null && why.Contains("\"Example Patcher\"", StringComparison.Ordinal) && why.Contains("compression", StringComparison.Ordinal), "patcher and reason named: " + why);
		Equal<string?>(null, SynthesisStep.Failure("[INF] [T2] Starting Patcher A Run\n[INF] [T2] Finished patcher run\n"), "a clean run is no failure");
	}

	// Synthesis's log is judged by what each look added to it, as its whole text would be: it grows to megabytes in a long
	// run, and was read and searched whole every two seconds
	static void T_ASynthesisLogIsJudgedAsItGrows()
	{
		var noise = string.Concat(Enumerable.Range(0, 3000).Select(i => $"2026-10-27 16:04:{i % 60:00}.000 -04:00 [INF] [T{i % 9}] Patched record {i}\r\n"));
		const string first = "2026-10-27 16:04:36.640 -04:00 [INF] [T16] ================= Starting Patcher First Run =================\r\n";
		const string second = "2026-10-27 16:05:36.640 -04:00 [INF] [T16] ================= Starting Patcher Second Patcher Run =================\r\n";
		const string third = "2026-10-27 16:06:36.640 -04:00 [INF] [T16] ================= Starting Patcher Third Run =================\r\n";
		const string finished = "2026-10-27 16:07:45.911 -04:00 [INF] [T2] Finished patcher run\r\n";
		var failed = first + noise + second + noise +
			"2026-10-27 16:05:45.899 -04:00 [ERR] [T35] System.NotImplementedException: Writing with compression enabled is not currently supported.\r\n" +
			"2026-10-27 16:05:45.907 -04:00 [ERR] [T35] Error while running:\r\n" + third + noise +
			"2026-10-27 16:06:45.899 -04:00 [ERR] [T35] a later error\r\n" + finished;
		var clean = first + noise + second + noise + finished;
		foreach (var (name, log) in new[] { ("a patcher that failed", failed), ("a clean run", clean), ("a run still going", first + noise + second), ("a log's last line without its end", failed.TrimEnd()) }) {
			// fed as it grows: in pieces that end anywhere, the last line not yet whole at some looks
			var digest = new SynthesisStep.LogDigest();
			var whole = "";
			for (var at = 0; at < log.Length; at += 7919) {
				var piece = log[at..Math.Min(log.Length, at + 7919)];
				var before = whole.Length - (whole.LastIndexOf('\n') + 1);
				whole += piece;
				var from = whole.Length - piece.Length - before;
				var end = whole.LastIndexOf('\n') + 1;
				if (end > from) {
					digest.Take(whole[from..end]);
				}
				Equal(SynthesisStep.Failure(whole), digest.FailureWith(whole[end..]), $"{name}, {whole.Length} letters in: judged by what was added as by the whole log");
			}
			digest.Restart();
			Equal<string?>(null, digest.Failure, $"{name}: a log begun anew is judged anew");
		}
		var why = SynthesisStep.Failure(failed);
		Check(why != null && why.Contains("\"Second Patcher\"", StringComparison.Ordinal) && why.Contains("compression", StringComparison.Ordinal) && !why.Contains("later", StringComparison.Ordinal),
			"the first error, and the patcher that had started when it came: " + why);
		Equal(why, SynthesisStep.Failure(failed.TrimEnd()), "the same of a log whose last line has no end yet");
		Equal<string?>(null, SynthesisStep.Failure(clean), "a clean run is no failure");
		Equal<string?>(null, SynthesisStep.Failure(first + noise + second), "a run still going is not judged");
	}

	// seen in a real run: a window being destroyed answers UI Automation with a raw COM error - a "gone" element, never
	// a failed step
	static void T_AWindowGoingAwayIsNotAFailure()
	{
		Check(Uia.Gone(new System.Runtime.InteropServices.COMException("Catastrophic failure", unchecked((int)0x8000FFFF))), "E_UNEXPECTED = gone");
		Check(Uia.Gone(new System.Windows.Automation.ElementNotAvailableException()), "not available = gone");
		Check(!Uia.Gone(new System.Runtime.InteropServices.COMException("Unspecified error", unchecked((int)0x80004005))), "E_FAIL is a real fault");
		Check(!Uia.Gone(new InvalidOperationException()), "anything else is a real fault");
	}

	// seen in a real run: xLODGen wrote where MO2's entry for it said (-o:), not into the step's output folder
	static void T_XLodGenWritesWhereTheStepSays()
	{
		var mo2Args = Mo2Instance.Unescape(@"-o:\""E:\\Tools\\Old Scratch\\Terrain LOD\"" -sse");
		Equal(@"-o:""E:\Tools\Old Scratch\Terrain LOD"" -sse", mo2Args, "MO2's ini escaping undone");
		Equal(@"-o:""C:\New\Terrain LOD"" -sse", XLodGenStep.WithOutput(mo2Args, @"C:\New\Terrain LOD\"), "an -o: there is replaced");
		Equal(@"-sse -o:""C:\New""", XLodGenStep.WithOutput("-sse -o:C:\\Old", @"C:\New"), "an unquoted one too");
		Equal(@"-o:""C:\New"" -sse", XLodGenStep.WithOutput("-sse", @"C:\New"), "none there: put first");
		Equal(@"-o:""C:\New""", XLodGenStep.WithOutput("", @"C:\New"), "no arguments at all");
		Equal(@"-sse -d:""C:\Game Copy\Data""", XLodGenStep.WithData("-sse", @"C:\Game Copy"), "the game Mod Organizer runs named");
		Equal(@"-sse -d:""E:\Mine""", XLodGenStep.WithData(@"-sse -d:""E:\Mine""", @"C:\Game Copy"), "a data folder the player set stays");
	}

	// seen in a real run: xLODGen had moved its output into its own mod, so ACMOS read an empty scratch folder
	// ("Invalid Path to LOD") - ACMOS paints the terrain LOD wherever it is
	static void T_AcmosFindsTheTerrainLodWhereItIs()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner test acmos " + Guid.NewGuid().ToString("N"));
		var scratch = Path.Combine(dir, "Terrain LOD");
		var mod = Path.Combine(dir, "mods", "SSELODGen_Output - X");
		try {
			Equal(null, AcmosStep.LodFolder(scratch, mod), "neither holds terrain LOD");
			Directory.CreateDirectory(Path.Combine(mod, "textures", "terrain", "tamriel"));
			File.WriteAllText(Path.Combine(mod, "textures", "terrain", "tamriel", "a.dds"), "x");
			Directory.CreateDirectory(scratch);
			Equal(mod, AcmosStep.LodFolder(scratch + "\\", mod + "\\"), "an empty scratch: the mod xLODGen moved it into");
			Directory.CreateDirectory(Path.Combine(scratch, "textures", "terrain"));
			File.WriteAllText(Path.Combine(scratch, "textures", "terrain", "readme.txt"), "x");
			Equal(mod, AcmosStep.LodFolder(scratch, mod), "a stray file in the scratch is no terrain LOD");
			File.WriteAllText(Path.Combine(scratch, "textures", "terrain", "b.dds"), "x");
			Equal(scratch, AcmosStep.LodFolder(scratch, mod), "the scratch folder first when it holds the LOD");
			// what stands between quote marks on ACMOS's command line holds none itself
			Equal<string?>(null, AcmosStep.QuoteIn("Paths Only", scratch, null), "a road set and a folder as they are");
			Check(AcmosStep.QuoteIn("Paths Only", "Roads\" -o:\"C:\\Somewhere else") is { } quoted && RunnerErrors.Find(quoted)?.Code == "TR-318",
				"a setting that would end its quote early and pass ACMOS arguments of its own is refused, with what to do");
			// a folder a run empties into its backup is never a whole drive, nor a folder the player or Windows keeps other things in
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (home != "") {
				Equal<string?>("a whole drive", StepKit.KeptFolder(Path.GetPathRoot(home)!), "a drive's own root");
				Equal<string?>("your user folder", StepKit.KeptFolder(home + "\\"), "the player's own folder");
				Check(StepKit.KeptFolder(Path.GetDirectoryName(home)!) != null, "a folder that holds it");
				Equal<string?>("your Downloads folder", StepKit.KeptFolder(Path.Combine(home, "downloads")), "their downloads, however it is spelled");
				Equal<string?>(null, StepKit.KeptFolder(Path.Combine(home, "Downloads", "Modding", "xLODGen Output")), "a folder of its own inside one of them is fine");
			}
			Equal<string?>(null, StepKit.KeptFolder(scratch), "and so is the tool's own scratch folder");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// ACMOS itself, with this test program standing in for it (Main's -autorun): the LOD already in the mod is painted THERE
	// (never moved into itself), a move that fails half way puts the plain textures back, and a "reads" in MO2's folders is refused
	static void T_AcmosPaintsTheLodInTheModItIsIn()
	{
		using var f = new FakeMo2();
		var keepDesktop = HiddenDesktop.OnPlayersDesktop;
		HiddenDesktop.OnPlayersDesktop = _ => [];
		var logs = Path.Combine(AppContext.BaseDirectory, "logs");
		var hadLogs = Directory.Exists(logs);
		try {
			var scratch = Path.Combine(f.Root, "Terrain LOD");
			Directory.CreateDirectory(scratch);
			f.Mod("Terrain LOD Mod");
			var tex = Path.Combine(f.Mods, "Terrain LOD Mod", "textures", "terrain", "tamriel");
			Directory.CreateDirectory(tex);
			foreach (var n in new[] { "a.dds", "c.dds" }) {
				File.WriteAllText(Path.Combine(tex, n), "plain");
			}
			// this test program itself (its apphost beside it, however the tests are started)
			var standIn = Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(typeof(Program).Assembly.Location) + ".exe");
			var settings = new JsonObject { ["exe"] = standIn, ["output_mod"] = "Terrain LOD Mod", ["reads"] = scratch, ["min_files"] = 1, ["roads"] = "Paths Only" };
			f.WriteSetup(("acmos", settings));
			var ctx = StepContext(f, "acmos");
			new AcmosStep().Run(ctx, new StepSettings(settings));
			Equal("painted", File.ReadAllText(Path.Combine(tex, "a.dds")), "painted in the mod");
			Equal(0, Directory.GetFileSystemEntries(scratch).Length, "the empty scratch was never moved into the mod");
			var told = Directory.GetDirectories(logs).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, "log.log")).Where(File.Exists).Select(File.ReadAllText).Last();
			Check(told.Contains(Path.Combine(f.Mods, "Terrain LOD Mod"), StringComparison.OrdinalIgnoreCase), "ACMOS was given the mod's LOD: " + told);
			Check(ctx.Journal.Kept.All(k => k.Done), "the plain textures are never put back over the painted ones");
			Check(ctx.PendingEnable.Contains("Terrain LOD Mod"), "the mod goes on after the step");
			ctx.Log.Dispose();

			// the move fails half way (c.dds held open): the plain textures come back where they could
			foreach (var n in new[] { "a.dds", "c.dds" }) {
				File.WriteAllText(Path.Combine(tex, n), "plain");
			}
			var ctx2 = StepContext(f, "acmos");
			using (new FileStream(Path.Combine(tex, "c.dds"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
				Throws<StepFailedException>(() => new AcmosStep().Run(ctx2, new StepSettings(settings)), "a failed move fails the step");
			}
			Equal("plain", File.ReadAllText(Path.Combine(tex, "a.dds")), "a texture already replaced is put back");
			Equal("plain", File.ReadAllText(Path.Combine(tex, "c.dds")), "the one that could not be replaced stays plain");
			ctx2.Log.Dispose();

			// "hidden" switched off for the step (a setting lets players turn it off): the tool is started in the open - no
			// hidden desktop, and so no guard that looks over the player's screen for it
			var looks = 0;
			HiddenDesktop.OnPlayersDesktop = _ => {
				Interlocked.Increment(ref looks);
				return [];
			};
			var open = (JsonObject)settings.DeepClone();
			open["hidden"] = false;
			var ctx3 = StepContext(f, "acmos");
			new AcmosStep().Run(ctx3, new StepSettings(open));
			Equal("painted", File.ReadAllText(Path.Combine(tex, "a.dds")), "painted with the tool in the open");
			Equal(0, looks, "no hidden desktop was made for it");
			ctx3.Log.Dispose();

			// "reads" inside MO2's mods folder: refused before a run
			var bad = new JsonObject { ["exe"] = standIn, ["output_mod"] = "Terrain LOD Mod", ["reads"] = Path.Combine(f.Mods, "Terrain LOD Mod") };
			var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
			var c = new CheckContext { Setup = Setup.Load(f.SetupFile), Mo2 = mo2, Profile = ProfileOf(f) };
			Check(new AcmosStep().Check(c, new StepSettings(bad)).Any(i => i.Blocking && i.Text.Contains("mods folder", StringComparison.Ordinal)), "the mod as its own source is refused");
		} finally {
			HiddenDesktop.OnPlayersDesktop = keepDesktop;
			if (!hadLogs && Directory.Exists(logs)) {
				Directory.Delete(logs, true);
			}
		}
	}

	// a step run on its own, as a run would (its record, log, backup folder and MO2 control)
	static RunContext StepContext(FakeMo2 a_f, string a_step, DevBench? a_devBench = null, CancellationToken a_cancel = default, IGuide? a_guide = null)
	{
		var s = Setup.Load(a_f.SetupFile);
		var mo2 = Mo2Instance.Load(s.Mo2Exe, s.Mo2Instance);
		var id = Guid.NewGuid().ToString("N")[..8];
		var j = new Journal { Id = id, Folder = Path.Combine(Runner.RunsDir(a_f.Data), id), SetupFile = Path.GetFullPath(a_f.SetupFile), SetupName = "Test", Profile = "Test",
			Mo2Exe = s.Mo2Exe, Mo2Instance = s.Mo2Instance, BackupDir = Path.Combine(a_f.Backups, id), Started = DateTime.Now, State = RunState.Running,
			Steps = [new StepRecord { Key = a_step, State = StepState.Running }] };
		Directory.CreateDirectory(j.Folder);
		j.Save();
		var log = new RunLog(j.Folder);
		return new RunContext { Setup = s, Mo2 = mo2, Profile = new Profile(mo2, "Test", () => false), Control = new Mo2Control(mo2, "Test", log), Log = log,
			Journal = j, Cancel = a_cancel, BackupFolder = j.BackupDir, DevBench = a_devBench ?? new(), Guide = a_guide ?? new NoGuide() };
	}

	// the stand-in for ACMOS Road Generator: every terrain texture of -l: painted into -o:, and the log ACMOS writes beside itself
	// (naming the folder it was given)
	static int FakeAcmos(string[] a_args)
	{
		string Arg(string a_p) => a_args.First(a => a.StartsWith(a_p, StringComparison.Ordinal))[a_p.Length..];
		var lod = Arg("-l:");
		var outDir = Arg("-o:");
		foreach (var t in Directory.EnumerateFiles(Path.Combine(lod, "textures", "terrain"), "*.dds", SearchOption.AllDirectories)) {
			var dst = Path.Combine(outDir, Path.GetRelativePath(lod, t));
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			File.WriteAllText(dst, "painted");
		}
		var log = Path.Combine(AppContext.BaseDirectory, "logs", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fffffff", System.Globalization.CultureInfo.InvariantCulture));
		Directory.CreateDirectory(log);
		File.WriteAllText(Path.Combine(log, "log.log"), $"read {lod}\nAll Done!\n");
		return 0;
	}

	// real runs ticked xLODGen's LOD levels, the first list in its options, instead of its worldspaces
	static void T_TheWorldspaceListIsTheOneWithNames()
	{
		IReadOnlyList<string> levels = ["", "4", "8", "16", "32"];
		IReadOnlyList<string> worlds = ["Tamriel [WRLD:0000003C]", "DLC2SolstheimWorld [WRLD:04000800]", "MarkarthWorld [WRLD:00016BB4]"];
		Equal<int?>(1, XLodGenStep.PickWorldspaces([levels, worlds]), "the named rows, not the LOD levels before them");
		Equal<int?>(0, XLodGenStep.PickWorldspaces([worlds, levels]), "whichever comes first");
		Equal<int?>(null, XLodGenStep.PickWorldspaces([levels]), "numbers only: no worldspace list yet");
		Equal<int?>(null, XLodGenStep.PickWorldspaces([]), "no list at all");
	}

	static void T_SynthesisLoadingErrorIsNotAFailure()
	{
		var t = new DateTime(2026, 10, 27, 23, 29, 20, DateTimeKind.Utc);
		DateTime? since = null;
		Check(!SynthesisStep.StillBlocked(ref since, true, t), "a blocking error just seen is not judged yet");
		Check(!SynthesisStep.StillBlocked(ref since, false, t.AddSeconds(5)), "gone 5 s later (its settings loaded)");
		Check(!SynthesisStep.StillBlocked(ref since, true, t.AddSeconds(40)), "seen again: its clock starts over");
		Check(!SynthesisStep.StillBlocked(ref since, true, t.AddSeconds(69)), "29 s later still not judged");
		Check(SynthesisStep.StillBlocked(ref since, true, t.AddSeconds(70)), "30 s without a break: the step fails");
	}

	// a run that finished stays finished when an after-run command fails: said in its notes, the profile put back once, the
	// command run once
	static void T_AfterRunCommandFailingKeepsTheRunFinished()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		var s = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var marker = Path.Combine(f.Root, "after ran.txt");
		s.Root["afterRun"] = new JsonArray($"echo ran>>\"{marker}\" & exit /b 5", $"echo next>>\"{marker}\"");
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(j.Notes.Any(n => n.Contains("exit code 5", StringComparison.Ordinal)), "the failed command is in the run's notes: " + string.Join("; ", j.Notes));
		Equal("ran|next", string.Join("|", File.ReadAllLines(marker).Select(l => l.Trim())), "each command ran once, and the one after the failed one too");
		Check(ProfileOf(f).IsOn("Output A"), "the built output on");
	}

	static void T_BeforeRunCommandMustSucceed()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		var s = f.WriteSetup(("landscapefixer", []));
		s.Root["beforeRun"] = new JsonArray("exit /b 3");
		s.Save();
		var before = f.Text("modlist.txt");
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.State == RunState.Failed && r.Verdict.Contains("exit code 3", StringComparison.Ordinal), "refused when the before-run command fails: " + r.Verdict);
		Equal(before, f.Text("modlist.txt"), "nothing changed");
	}

	static void T_LowDiskRefused()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		var s = f.WriteSetup(("landscapefixer", []));
		s.Root["minFreeGb"] = 1_000_000;
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.Verdict.StartsWith("not started", StringComparison.Ordinal) && r.Verdict.Contains("GB free", StringComparison.Ordinal), r.Verdict);
	}

	static void T_MissingProfileRefused()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("landscapefixer", []));
		((JsonObject)s.Root["mo2"]!)["profile"] = "Nope";
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.Verdict.Contains("no profile", StringComparison.Ordinal), r.Verdict);
	}

	static void T_CloseWaitsForADownloadToEnd()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Directory.CreateDirectory(mo2.DownloadsDir);
		var dl = Path.Combine(mo2.DownloadsDir, "Some Mod.7z.unfinished");
		File.WriteAllText(dl, "");
		var open = true;
		var c = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => open };
		var t = Task.Run(() => {
			Thread.Sleep(1500);
			File.Delete(dl);
			open = false;
		});
		var sw = Stopwatch.StartNew();
		c.Close();
		t.Wait();
		Check(sw.Elapsed >= TimeSpan.FromSeconds(1.4), "it waited for the download instead of closing MO2 under it");
	}

	static void T_CloseGivesUpOnALongDownload()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Directory.CreateDirectory(mo2.DownloadsDir);
		File.WriteAllText(Path.Combine(mo2.DownloadsDir, "Big.7z.unfinished"), "");
		var keep = Mo2Control.BusyWait;
		Mo2Control.BusyWait = TimeSpan.FromSeconds(1);
		try {
			var c = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => true };
			Throws<Mo2BusyException>(() => c.Close(), "a download that never ends");
		} finally {
			Mo2Control.BusyWait = keep;
		}
	}

	// MO2's readiness, timed short; the statics go back after each test
	static void FastReady(Action a_body)
	{
		var (q, l, p) = (Mo2Control.ReadyQuiet, Mo2Control.ReadyLimit, Mo2Control.Poll);
		Mo2Control.ReadyQuiet = TimeSpan.FromSeconds(1);
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(8);
		Mo2Control.Poll = TimeSpan.FromMilliseconds(100);
		try {
			a_body();
		} finally {
			(Mo2Control.ReadyQuiet, Mo2Control.ReadyLimit, Mo2Control.Poll) = (q, l, p);
		}
	}

	static void T_ReadyWaitsWhileMo2StillLoads() => FastReady(() => {
		using var f = new FakeMo2();
		var sw = Stopwatch.StartNew();
		// a full core busy for 2 s (its plugins loading), then quiet
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			RunningProbe = () => true,
			ReadyProbe = () => new(true, true, sw.Elapsed < TimeSpan.FromSeconds(2) ? sw.Elapsed : TimeSpan.FromSeconds(2), null),
		};
		c.WaitReady(default);
		Check(sw.Elapsed >= TimeSpan.FromSeconds(2.9), $"ready after {sw.Elapsed.TotalSeconds:0.0} s - before MO2 finished loading");
		Check(sw.Elapsed < TimeSpan.FromSeconds(6), $"ready only after {sw.Elapsed.TotalSeconds:0.0} s - a quiet MO2 was not taken as ready");
	});

	static void T_ReadyNowWhenMo2IsAlreadyQuiet() => FastReady(() => {
		using var f = new FakeMo2();
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { ReadyProbe = () => new(true, true, TimeSpan.FromSeconds(9), null) };
		var sw = Stopwatch.StartNew();
		c.WaitReady(default);
		Check(sw.Elapsed < TimeSpan.FromSeconds(2), $"a quiet MO2 waited {sw.Elapsed.TotalSeconds:0.0} s");
	});

	static void T_ReadyGivesUpNamingWhy() => FastReady(() => {
		using var f = new FakeMo2();
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(1);
		var hung = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { ReadyProbe = () => new(true, false, TimeSpan.Zero, null) };
		try {
			hung.WaitReady(default);
			Check(false, "a hung MO2 was taken as ready");
		} catch (StepFailedException e) {
			Check(e.Message.Contains("does not answer", StringComparison.Ordinal), e.Message);
		}
		var boxed = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { ReadyProbe = () => new(true, true, TimeSpan.Zero, "Some plugins failed to load") };
		try {
			boxed.WaitReady(default);
			Check(false, "an MO2 with a box open was taken as ready");
		} catch (StepFailedException e) {
			Check(e.Message.Contains("plugins failed", StringComparison.Ordinal), e.Message);
		}
	});

	static void T_LostRequestIsAnsweredAndAskedAgain() => FastReady(() => {
		using var f = new FakeMo2();
		int launches = 0, pressed = 0;
		string? box = null;
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, box),
			PressBoxProbe = _ => { ++pressed; box = null; },
			// the first request reaches an MO2 still loading: it shows its box and no tool starts; the second one works
			Launch = (_, _) => { if (++launches == 1) { box = "failed to receive data from secondary process: Unknown error"; } return 1; },
		};
		var pid = c.LaunchTool("run -e Tool", () => launches >= 2 ? 4242 : 0, TimeSpan.FromSeconds(3), default);
		Equal<int?>(4242, pid, "the tool's process");
		Equal(2, launches, "requests sent");
		Equal(1, pressed, "boxes answered");
	});

	static void T_ToolThatNeverStartsIsReported() => FastReady(() => {
		using var f = new FakeMo2();
		var launches = 0;
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, null),
			Launch = (_, _) => ++launches,
		};
		var pid = c.LaunchTool("run -e Tool", () => 0, TimeSpan.FromSeconds(1), default);
		Equal<int?>(null, pid, "no process");
		Equal(1, launches, "a request with no box is not sent again");
	});

	// two MO2 starts in the same second: the loser's "already running" box is answered OK, and MO2 is then ready
	// only within a minute of a start this run sent; a box of a copy the player started is left alone (and named on time-out)
	static void T_AlreadyRunningBoxIsAnswered() => FastReady(() => {
		using var f = new FakeMo2();
		var pressed = 0;
		string? box = "An instance of Mod Organizer is already running";
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			RunningProbe = () => false,
			LeftoverProbe = () => [],
			Launch = (_, _) => 1,
			ReadyProbe = () => new(true, true, TimeSpan.Zero, box),
			PressBoxProbe = _ => { ++pressed; box = null; },
		};
		c.Start(default);
		Equal(1, pressed, "boxes answered after our own start");
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(1);
		var notOurs = 0;
		var theirs = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, "An instance of Mod Organizer is already running"),
			PressBoxProbe = _ => ++notOurs,
		};
		Throws<StepFailedException>(() => theirs.WaitReady(default), "a box we did not cause keeps MO2 not ready");
		Equal(0, notOurs, "a box of a copy the player started was pressed");
	});

	// a box that stays open after OK cannot hold the ready wait past its limit
	static void T_ABoxThatStaysOpenEndsTheWait() => FastReady(() => {
		using var f = new FakeMo2();
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(1);
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, "failed to receive data from secondary process"),
			PressBoxProbe = _ => { },
		};
		var sw = Stopwatch.StartNew();
		Throws<StepFailedException>(() => c.WaitReady(default), "an endless box");
		Check(sw.Elapsed < TimeSpan.FromSeconds(4), $"the wait ran {sw.Elapsed.TotalSeconds:0.0} s past a 1 s limit");
	});

	// Start itself waits for the leftover before it launches MO2
	static void T_StartWaitsForTheLeftoverFirst() => FastReady(() => {
		using var f = new FakeMo2();
		var keep = Mo2Control.LeftoverWait;
		Mo2Control.LeftoverWait = TimeSpan.FromSeconds(3);
		try {
			int probes = 0, probesAtLaunch = -1;
			var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				RunningProbe = () => false,
				LeftoverProbe = () => ++probes < 3 ? [(99003, "TheWatcherDump.exe")] : [],
				Launch = (_, _) => { probesAtLaunch = probes; return 1; },
				ReadyProbe = () => new(true, true, TimeSpan.Zero, null),
			};
			c.Start(default);
			Equal(3, probesAtLaunch, "probe calls when MO2 was launched (only after the leftover was gone)");
		} finally {
			Mo2Control.LeftoverWait = keep;
		}
	});

	// a finished download MO2 left as "<file>.unfinished" beside the same bytes is no download in progress; a real one still is
	static void T_StaleUnfinishedDownloadIsNotInProgress()
	{
		var d = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "dl " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(d);
		try {
			File.WriteAllText(Path.Combine(d, "Done.zip"), "same bytes");
			File.WriteAllText(Path.Combine(d, "Done.zip.unfinished"), "same bytes");
			File.WriteAllText(Path.Combine(d, "Going.zip.unfinished"), "part");
			File.WriteAllText(Path.Combine(d, "Diff.zip"), "the whole file");
			File.WriteAllText(Path.Combine(d, "Diff.zip.unfinished"), "the whole fil?");
			var (active, stale) = Mo2Control.Downloads(d);
			Equal("Diff.zip.unfinished, Going.zip.unfinished", string.Join(", ", active.Order()), "still downloading");
			Equal("Done.zip.unfinished", string.Join(", ", stale), "finished, left behind");
		} finally {
			Directory.Delete(d, true);
		}
	}

	// a program still running from MO2's folders (a dead game's crash dumper) holds MO2's start: waited for, then named
	static void T_LeftoverFromMo2FoldersHoldsTheStart()
	{
		using var f = new FakeMo2();
		var keep = Mo2Control.LeftoverWait;
		Mo2Control.LeftoverWait = TimeSpan.FromMilliseconds(400);
		try {
			var calls = 0;
			var ends = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				LeftoverProbe = () => ++calls < 3 ? [(99001, "TheWatcherDump.exe")] : [],
			};
			ends.WaitForLeftovers(default);
			Check(calls >= 3, "the start went on while the leftover still ran");
			var stays = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				LeftoverProbe = () => [(99002, "SkyrimDiagHelper.exe")],
			};
			try {
				stays.WaitForLeftovers(default);
				Check(false, "MO2 would start beside a leftover program");
			} catch (StepFailedException e) {
				Check(e.Message.Contains("SkyrimDiagHelper.exe (process 99002)", StringComparison.Ordinal), e.Message);
			}
		} finally {
			Mo2Control.LeftoverWait = keep;
		}
	}

	// a Steam copy of the game: Steam started first when it is not running; a GOG copy, or Steam already up, starts nothing
	static void T_SteamStartedBeforeTheGame() => FastReady(() => {
		using var f = new FakeMo2();
		var keep = Mo2Control.SteamSettle;
		Mo2Control.SteamSettle = TimeSpan.Zero;
		try {
			const string steamGame = @"E:\Games\SteamLibrary\steamapps\common\Skyrim Special Edition";
			Check(Mo2Control.IsSteamGame(steamGame), "a Steam library path");
			Check(!Mo2Control.IsSteamGame(@"C:\GOG Games\Skyrim Anniversary Edition"), "a GOG path");
			Check(!Mo2Control.IsSteamGame(f.Game), "a copied game folder with no Steam library");
			File.WriteAllText(Path.Combine(f.Game, "steam_api64.dll"), "");
			Check(Mo2Control.IsSteamGame(f.Game), "a Steam copy moved out of steamapps (a modlist's Stock Game)");
			var up = false;
			var cmds = new List<string>();
			var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				SteamRunningProbe = () => up,
				SteamExeProbe = () => @"C:\Program Files (x86)\Steam\steam.exe",
				Launch = (cmd, _) => { cmds.Add(cmd); up = true; return 1; },
			};
			c.EnsureSteam(@"C:\GOG Games\Skyrim Anniversary Edition", default);
			Equal(0, cmds.Count, "nothing started for a GOG game");
			c.EnsureSteam(steamGame, default);
			Equal(1, cmds.Count, "Steam started once");
			Check(cmds[0].Contains("steam.exe\" -silent", StringComparison.OrdinalIgnoreCase), cmds[0]);
			c.EnsureSteam(steamGame, default);
			Equal(1, cmds.Count, "Steam already up - not started again");
			var none = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { SteamRunningProbe = () => false, SteamExeProbe = () => null };
			Throws<StepFailedException>(() => none.EnsureSteam(steamGame, default), "no Steam to start");
		} finally {
			Mo2Control.SteamSettle = keep;
		}
	});

	// LaunchGame starts Steam BEFORE it asks MO2 for the game
	static void T_LaunchGameStartsSteamFirst() => FastReady(() => {
		using var f = new FakeMo2();
		var keep = Mo2Control.SteamSettle;
		Mo2Control.SteamSettle = TimeSpan.Zero;
		try {
			File.WriteAllText(Path.Combine(f.Game, "steam_api64.dll"), "");
			bool steam = false, mo2 = false;
			var cmds = new List<string>();
			var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				RunningProbe = () => mo2,
				LeftoverProbe = () => [],
				SteamRunningProbe = () => steam,
				SteamExeProbe = () => @"C:\Program Files (x86)\Steam\steam.exe",
				ReadyProbe = () => new(true, true, TimeSpan.Zero, null),
				Launch = (cmd, _) => {
					cmds.Add(cmd);
					steam |= cmd.Contains("steam.exe", StringComparison.OrdinalIgnoreCase);
					mo2 |= cmd.Contains("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase);
					return 1;
				},
			};
			var pid = c.LaunchGame("SKSE", () => cmds.Any(x => x.Contains("run -e", StringComparison.Ordinal)) ? 4243 : 0, default);
			Equal(4243, pid, "the game's process");
			var s = cmds.FindIndex(x => x.Contains("steam.exe\" -silent", StringComparison.OrdinalIgnoreCase));
			var g = cmds.FindIndex(x => x.Contains("run -e", StringComparison.Ordinal));
			Check(s >= 0 && g > s, $"Steam at {s}, the game start at {g}: " + string.Join(" | ", cmds));
			Check(cmds.All(x => x.Contains("steam.exe", StringComparison.OrdinalIgnoreCase) || x.Contains(f.Exe, StringComparison.OrdinalIgnoreCase)),
				"a start outside the fake MO2: " + string.Join(" | ", cmds));
		} finally {
			Mo2Control.SteamSettle = keep;
		}
	});

	// the start wait answers each box through its own press: the plugin error's No, a notice's button; an unknown one stops it
	static void T_NativeBoxesArePressedByTheirVerdict()
	{
		var pressed = new List<string>();
		InGame.AnswerNativeBoxes([
			new InGame.NativeBox("Plugin error", "SKSE plugin X.dll failed to load. Continue running?", ["Yes", "No"], l => pressed.Add("1:" + l)),
			new InGame.NativeBox("Notice", "Settings reset.", ["OK"], l => pressed.Add("2:" + l)),
		], RunLog.Null);
		Equal("1:No, 2:OK", string.Join(", ", pressed), "buttons pressed");
		pressed.Clear();
		Throws<StepFailedException>(() => InGame.AnswerNativeBoxes([
			new InGame.NativeBox("Question", "Overwrite your settings?", ["Yes", "No"], l => pressed.Add("3:" + l))], RunLog.Null), "an unknown box");
		Equal(0, pressed.Count, "an unknown box was pressed");
	}

	// a Windows box of the game before DevBench answers: SKSE's plugin error -> No, a notice -> its button, anything else stops
	static void T_GameWindowsBoxesBeforeDevBench()
	{
		var (press, note) = InGame.NativeBoxVerdict("Plugin error",
			"SKSE plugin ENBWorldspaceWeatherlists.dll failed to load. Continue running the game?", ["&Yes", "&No"]);
		Equal("No", press, "SKSE's plugin error");
		Check(note.Contains("ENBWorldspaceWeatherlists.dll", StringComparison.Ordinal), note);
		Equal("OK", InGame.NativeBoxVerdict("Notice", "Your settings were reset.", ["OK"]).Press, "a one-button notice");
		Equal<string?>(null, InGame.NativeBoxVerdict("Question", "Overwrite your settings?", ["Yes", "No"]).Press, "an unknown question");
		// a box whose one button is Cancel shows something going on: pressing it would call that off
		var (cancel, why) = InGame.NativeBoxVerdict("Working", "Rebuilding the cache, please wait.", ["Cancel"]);
		Check(cancel == null && why.StartsWith("the game shows a Windows box this step does not answer", StringComparison.Ordinal) && why.Contains("Rebuilding the cache", StringComparison.Ordinal),
			"a lone Cancel is never pressed, and the box is named: " + why);
		// Windows gives a plain notice's lone OK the control id of Cancel: only the button's own words tell the two apart
		Equal("OK", string.Join(",", Uia.BoxNames([("Cancel", "OK")], "Abbrechen")), "a notice's lone OK is named by its words");
		Equal("Aceptar", string.Join(",", Uia.BoxNames([("Cancel", "Aceptar")], "Cancelar", ["Aceptar", "Cerrar"])), "in Windows' own language too");
		Equal("Cancel", string.Join(",", Uia.BoxNames([("Cancel", "Aceptar")], "Cancel", [])), "words nobody here knows under Cancel's id stay a Cancel");
		Equal("Cancel", string.Join(",", Uia.BoxNames([("Cancel", "Abbrechen")], "Cancel", ["OK", "Close"])), "a program's own language need not be Windows': its Cancel is never taken for a notice's button");
		Equal("Close", string.Join(",", Uia.BoxNames([("Cancel", "Close")], "Abbrechen")), "a lone Close under Cancel's id acknowledges");
		Equal("Cancel", string.Join(",", Uia.BoxNames([("Cancel", "Cancel")], "")), "a lone Cancel is a Cancel");
		Equal("Cancel", string.Join(",", Uia.BoxNames([("Cancel", "abbrechen")], "Abbrechen")), "in Windows' own language too");
		// Windows' own words for its buttons on a German Windows
		static string German(string a_standard) => a_standard switch { "Yes" => "Ja", "No" => "Nein", "Cancel" => "Abbrechen", "Close" => "Schliessen", "OK" => "OK", _ => "" };
		Equal("Yes,No", string.Join(",", Uia.BoxNames([("Yes", "Ja"), ("No", "Nein")], "Abbrechen", null, German)), "two buttons: each by what it is, in Windows' own words");
		Equal("OK,Cancel", string.Join(",", Uia.BoxNames([("OK", "OK"), ("Cancel", "Abbrechen")], "Abbrechen")), "a Cancel beside an OK keeps its name");
		Equal("Repack now", string.Join(",", Uia.BoxNames([(null, "Repack now")], "")), "a button of the program's own by its words");
		Equal("Cancel", string.Join(",", Uia.BoxNames([(null, "Abbrechen")], "Abbrechen")), "a lone Cancel by its words alone, whatever its id");
		Equal("OK", string.Join(",", Uia.BoxNames([("OK", "OK")], "Abbrechen")), "a lone OK under its own id");
		Equal("Close", string.Join(",", Uia.BoxNames([("Close", "Schliessen")], "Abbrechen", null, German)), "a lone Close stays a notice's button");
		// A program's own buttons under the ids of standard ones keep their own words (TexGen's last question, as a real run met it: read
		// by the ids alone it was a question with a No, and the run stopped on a TexGen that had finished)
		Equal("Exit TexGen,Zip and Exit,Check log,Restart", string.Join(",", Uia.BoxNames([("OK", "Exit TexGen"), ("Cancel", "Zip and Exit"), ("Yes", "Check log"), ("No", "Restart")], "Cancel", null, German)),
			"a program's own words under standard ids are never a Yes, a No or an OK");
		Equal("Zip and Exit", string.Join(",", Uia.BoxNames([("OK", "Zip and Exit")], "Cancel")), "nor is a lone button of its own an OK to acknowledge");
		Equal("Ja,Nein", string.Join(",", Uia.BoxNames([("Yes", "Ja"), ("No", "Nein")], "Cancel")), "words Windows does not use here stay the program's own");
	}

	// a mod's box that promises a second one (perks being reset) keeps the settle going; an ordinary notice does not
	static void T_BoxThatPromisesAnotherIsWaitedFor()
	{
		Check(InGame.PromisesBox("Ordinator: perk reset in progress. A message box will appear when the process is complete."), "the promise");
		Check(!InGame.PromisesBox("Sacrosanct: scripts modified by another mod were found."), "a plain notice");
		Check(!InGame.PromisesBox("Your new settings will appear after you reload."), "'will appear' about something else");
		var t0 = new DateTime(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc);
		var plain = t0.AddSeconds(60);
		Equal(t0.AddMinutes(10), InGame.SettleUntil(plain, t0, "A message box will appear when the process is complete."), "the promise holds 10 minutes");
		Equal(t0.AddSeconds(80), InGame.SettleUntil(plain, t0.AddSeconds(70), "Perks reset."), "the promised box ends the hold (10 s more)");
		Equal(plain, InGame.SettleUntil(plain, t0.AddSeconds(5), "A notice."), "an early notice keeps the plain minute");
	}

	// Bottled Shaders' "[DDC]" line ends the compile watch on a warm start - unless a compile ("remaining=N" > 0) follows it
	static void T_BootPassOverEndsTheCompileWatch()
	{
		Check(InGame.BootPassOver(["[2026-10-28 19:33:10.000] [info] [ShaderTiming] x remaining=0", "[2026-10-28 19:33:54.000] [info] [DDC] 6651 entries, 0 trimmed"]),
			"warm start: the trim line stands");
		Check(!InGame.BootPassOver(["[19:33:54.000] [info] [DDC] 6651 entries", "[19:34:10.000] [info] [ShaderTiming] Very slow y remaining=41"]),
			"a compile after the trim line");
		Check(!InGame.BootPassOver(["[19:33:10.000] [info] [ShaderTiming] x remaining=12"]), "no trim line yet");
		Check(!InGame.BootPassOver(["Open Shaders 2.19.0 loaded"]), "a shader mod that writes no trim line");
		var start = new DateTime(2026, 11, 2, 19, 30, 0);
		Check(!InGame.BootPassOver(["[2026-11-01 22:10:00.000] [info] [DDC] 6651 entries"], start), "an earlier session's trim line");
		Check(InGame.BootPassOver(["[2026-11-02 19:30:40.000] [info] [DDC] 6651 entries"], start), "this start's trim line");
	}

	// the leak guard, both ways, with a harmless program (no window of any kind - the "player's screen" is the fake below)
	static void T_LeakGuardEndsAToolThatShowsOnThePlayersScreen()
	{
		var keep = HiddenDesktop.OnPlayersDesktop;
		HiddenDesktop.GuardPoll = TimeSpan.FromMilliseconds(100);
		try {
			// Windows' own ping, which opens no window of any kind: HiddenDesktop.Start gives a console program no console at
			// all (CREATE_NO_WINDOW)
			var ping = $"\"{Path.Combine(Environment.SystemDirectory, "PING.EXE")}\" -n 31 127.0.0.1";
			HiddenDesktop.OnPlayersDesktop = _ => [];
			using (var quiet = new HiddenDesktop("Tool-Runner-test-a")) {
				using var p = quiet.Start(ping, Path.GetTempPath());
				Thread.Sleep(800);
				Check(quiet.Leak == null && !p.HasExited, "a tool that stays hidden was ended");
				Procs.KillTree(p.Id);
			}
			HiddenDesktop.OnPlayersDesktop = pids => pids.Count > 0 ? [(nint)1] : [];
			using (var leaky = new HiddenDesktop("Tool-Runner-test-b")) {
				using var p = leaky.Start(ping, Path.GetTempPath());
				// (up to 20 s: the suite runs at Idle priority beside other builds, where the guard's look can be late; the tool
				// itself would run 30 s, so an end inside 20 s is still the guard's)
				var sw = Stopwatch.StartNew();
				while (!p.HasExited && sw.Elapsed < TimeSpan.FromSeconds(20)) {
					Thread.Sleep(100);
				}
				Check(p.HasExited, "a tool that showed on the player's screen was not ended");
				Check(leaky.Leak?.Contains("player's screen", StringComparison.Ordinal) == true, "no reason given: " + leaky.Leak);
			}
		} finally {
			HiddenDesktop.OnPlayersDesktop = keep;
			HiddenDesktop.GuardPoll = TimeSpan.FromMilliseconds(500);
		}
	}

	static void T_BlockedPutBackKeepsTheRestoreButton()
	{
		using var f = new FakeMo2();
		var plugins = Path.Combine(f.ProfileDir, "plugins.txt");
		// held open by another program for the whole put-back (it may be read, not replaced); a read-only mark alone no longer
		// blocks a write - the file is written and stays read-only
		FileStream? held = null;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			File.AppendAllText(plugins, "Stray.esp\r\n");
			held = new FileStream(plugins, FileMode.Open, FileAccess.Read, FileShare.Read);
		}));
		f.WriteSetup(("landscapefixer", []));
		try {
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(r.Verdict.Contains("could NOT be put back", StringComparison.Ordinal), "the failed put-back is said: " + r.Verdict);
			var j = Journal.Interrupted(Runner.RunsDir(f.Data));
			Check(j is { NeedsRestore: true }, "the run is offered for Restore");
			held!.Dispose();
			held = null;
			Equal(RunState.Restored, Runner.Restore(j!).State, "Restore works once the file is free");
			Check(!f.Text("plugins.txt").Contains("Stray.esp", StringComparison.Ordinal), "plugins.txt as before");
			Check(Journal.Interrupted(Runner.RunsDir(f.Data)) == null, "nothing left to restore");
		} finally {
			held?.Dispose();
		}
	}

	static void T_RetiredModsStayOffAfterTheRun()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			ctx.Journal.Retire.Add("Replaced");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A", ["replaces"] = new JsonArray("Replaced") }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		var p = ProfileOf(f);
		Check(!p.IsOn("Replaced"), "the replaced mod stays off");
		Check(!f.Text("plugins.txt").Contains("*Old.esp", StringComparison.Ordinal), "its plugin unticked");
	}

	// an old output that cannot come back (a file of it locked): the run says so (never "back as it was"), the mod stays off while
	// it is out - and once Restore puts it back, it is on again (it was never "replaced for good")
	static void T_OutputStillOutIsOffUntilItComesBack()
	{
		using var f = new FakeMo2();
		FileStream? held = null;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "landscapefixer", "Output A");
			var parked = ctx.Journal.Parked.Last(p => p.Mod == "Output A");
			held = new FileStream(Path.Combine(parked.To, "OutA.esp"), FileMode.Open, FileAccess.Read, FileShare.None);
			throw new StepFailedException("the tool crashed");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Equal(RunState.Failed, r.State, "failed");
			Check(r.Verdict.Contains("except Output A", StringComparison.Ordinal), "the verdict names what is still out: " + r.Verdict);
			Check(!ProfileOf(f).IsOn("Output A"), "kept off while its old output is out");
			var j = Journal.Interrupted(Runner.RunsDir(f.Data));
			Check(j is { NeedsRestore: true }, "Restore is offered");
			Check(!j!.Retire.Contains("Output A"), "never marked replaced for good");
			held!.Dispose();
			held = null;
			Equal(RunState.Restored, Runner.Restore(Journal.Load(j.Folder)).State, "Restore once the file is free");
			Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "its old output is back");
			var p = ProfileOf(f);
			Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "the profile as it was, Output A on again");
		} finally {
			held?.Dispose();
		}
	}

	// a learned-settings record that names a file outside its own folder (damaged or edited by hand) is refused, nothing read
	static void T_LearnedSettingsStayInTheirFolder()
	{
		using var f = new FakeMo2();
		var live = Path.Combine(f.Root, "tool.ini");
		File.WriteAllText(live, "mine=1");
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A", ["executable"] = "SomeTool" }));
		var setup = Setup.Load(f.SetupFile);
		ToolSettings.Keep(setup, "landscapefixer", "SomeTool", [live]);
		var rec = Path.Combine(ToolSettings.Folder(setup, "landscapefixer"), "learned.json");
		var o = JsonNode.Parse(File.ReadAllText(rec))!.AsObject();
		o["Files"]![0]!["Stored"] = @"..\..\..\..\Setups\Test.json";
		File.WriteAllText(rec, o.ToJsonString());
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "refused");
		Check(r.Verdict.Contains("outside their folder", StringComparison.Ordinal), "said why: " + r.Verdict);
		Equal("mine=1", File.ReadAllText(live), "the player's file untouched");
	}

	// the preview names the tools, what goes off and why, and what is replaced for good - and changes nothing
	static void T_PreviewSaysWhatARunDoes()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }),
			("synthesis", new JsonObject { ["output_mod"] = "Output B", ["replaces"] = new JsonArray("Replaced") }));
		var before = Profile.OrderFiles.Select(n => Path.Combine(f.ProfileDir, n)).Where(File.Exists).Select(File.ReadAllBytes).ToList();
		var setup = Setup.Load(f.SetupFile);
		var p = RunPreview.Build(setup, Mo2Instance.Load(f.Exe, f.Instance), Runner.Plan(setup, null, null, null));
		Equal(2, p.Steps.Count, "two tools");
		var off = p.OffForTheRun.ToDictionary(o => o.Mod, o => o.Why);
		Check(off.TryGetValue("Output A", out var a) && a.Contains("Landscape Fixer", StringComparison.Ordinal), "Output A off, made by Landscape Fixer");
		Check(off.ContainsKey("Output B") && off.ContainsKey("Replaced"), "Output B and the replaced mod off");
		Check(off.TryGetValue("Needs B", out var nb) && nb.Contains("OutB.esp", StringComparison.Ordinal), "a mod needing Output B's plugin off with it, said why");
		Check(!off.ContainsKey("Base Mod"), "nothing else");
		Equal("Replaced", p.ReplacedForGood.Single(), "the replaced mod is kept off after");
		var lines = p.Lines(k => k == "landscapefixer" ? TimeSpan.FromMinutes(3) : null);
		Check(lines.Any(l => l.Contains("usually 3 min", StringComparison.Ordinal)) && lines.Any(l => l.Contains("never timed", StringComparison.Ordinal)),
			"the usual times and the untimed step: " + string.Join(" | ", lines));
		var after = Profile.OrderFiles.Select(n => Path.Combine(f.ProfileDir, n)).Where(File.Exists).Select(File.ReadAllBytes).ToList();
		Check(before.Zip(after).All(x => x.First.SequenceEqual(x.Second)) && before.Count == after.Count, "the profile byte for byte as before");
		Check(!Directory.Exists(Runner.RunsDir(f.Data)) || Directory.GetDirectories(Runner.RunsDir(f.Data)).Length == 0, "no run record made");
	}

	// only the last few runs' backups stay - this setup's oldest go, another setup's never
	static void T_OnlyTheLastBackupsAreKept()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		setup.Root["keepBackups"] = 2;
		setup.Save();
		for (var d = 1; d <= 4; ++d) {
			var old = Path.Combine(f.Backups, $"2026-01-0{d} 00-00-00 Test");
			Directory.CreateDirectory(old);
			var ro = Path.Combine(old, "old.txt");
			File.WriteAllText(ro, "x");
			File.SetAttributes(ro, FileAttributes.ReadOnly);
		}
		Directory.CreateDirectory(Path.Combine(f.Backups, "2026-01-01 00-00-00 Other"));
		Directory.CreateDirectory(Path.Combine(f.Backups, "my own folder"));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		var left = Directory.GetDirectories(f.Backups).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
		Equal(4, left.Count, "this run's, the newest old one, the other setup's, the player's own folder: " + string.Join(" | ", left));
		Check(left.Contains("2026-01-04 00-00-00 Test") && !left.Contains("2026-01-03 00-00-00 Test"), "the newest old one stays, older ones go");
		Check(left.Contains("2026-01-01 00-00-00 Other") && left.Contains("my own folder"), "another setup's and a folder not of a run stay");
	}

	// the rename: saves, tasks and stamps made under the old name are still the runner's own
	static void T_OldNameIsStillOurs()
	{
		Check(Brand.Made(InGame.SaveClean), "a new save of ours");
		Check(Brand.Made("MO2 Tool Runner - clean save"), "a save made under the old name");
		Check(!Brand.Made("Save9 - Lydia"), "a player's save");
		Check(!Brand.Made(Brand.Name + "clean save"), "only the name followed by \" - \"");
		Check(Brand.AllNames.First() == Brand.Name && Brand.AllNames.Contains("MO2 Tool Runner"), "both names known, the new one first");
	}

	// runs at a set time are gone: only a task an older version made is recognised, and removed
	static void T_OldTimedRunsAreClearedAway()
	{
		Check(Schedule.IsOurs("Tool Runner - Bottled Shaders 1A2B3C"), "a task of ours");
		Check(Schedule.IsOurs("\\MO2 Tool Runner - Bottled Shaders 1A2B3C"), "one made under the old name");
		Check(!Schedule.IsOurs("MicrosoftEdgeUpdateTaskMachineCore"), "anyone else's task");
		Check(!Schedule.IsOurs("MO2 Tool Runner"), "only the name followed by \" - \"");
		Check(Schedule.IsOurs("mo2 tool runner - Bottled 1A2B3C"), "any case");
		Check(Schedule.StartsOurs("<Exec><Command>\"C:\\Some Folder\\MO2 Tool Runner.exe\"</Command></Exec>"), "a task starting the old program");
		Check(Schedule.StartsOurs("<Exec><Command>C:\\x\\Tool Runner.exe</Command></Exec>"), "or the new one");
		Check(!Schedule.StartsOurs("<Exec><Command>cmd.exe</Command></Exec>"), "a task of the player's own that only shares the name");
		// Windows itself, only where asked (harmless tasks a year ahead, removed at once)
		if (Environment.GetEnvironmentVariable("ToolRunnerScheduleProbe") != "1") {
			return;
		}
		const string probe = "MO2 Tool Runner - zz offline leftover", theirs = "MO2 Tool Runner - zz offline not ours";
		// what is tested is which task goes, never how fast Windows answers: a PC busy with other work took over 30 s
		var limit = Schedule.Limit;
		Schedule.Limit = TimeSpan.FromMinutes(3);
		try {
			Check(Schedule.AddProbe(probe) && Schedule.Exists(probe), "a leftover task made");
			Check(Schedule.AddProbe(theirs, "cmd.exe") && Schedule.Exists(theirs), "a same-named task of the player's made");
			Check(Schedule.RemoveLeftovers(n => n == probe || n == theirs).SequenceEqual([probe]), "only ours is removed");
			Check(!Schedule.Exists(probe) && Schedule.Exists(theirs), "Windows no longer has ours, still has theirs");
		} finally {
			Schedule.DeleteProbe(probe);
			Schedule.DeleteProbe(theirs);
			Schedule.Limit = limit;
		}
	}

	// The data kept in Windows' app-data folder - copied whole, its paths repointed, the old folder gone; never
	// over a folder that holds anything, and a copy that fails leaves the data where it was
	static void T_TheDataMovesWholeToTheAppDataFolder()
	{
		var root = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "data move " + Guid.NewGuid().ToString("N")[..8]);
		var from = Path.Combine(root, "beside", Brand.Name + " Data");
		var to = Path.Combine(root, "appdata", Brand.Name);
		try {
			Directory.CreateDirectory(Path.Combine(from, "Setups"));
			Directory.CreateDirectory(Path.Combine(from, "Runs", "2026-01-01 00-00-00 Test"));
			Directory.CreateDirectory(Path.Combine(from, "empty folder"));
			File.WriteAllText(Path.Combine(from, "Setups", "Test.json"), "{ \"name\": \"Test\" }");
			var record = new JsonObject { ["SetupFile"] = Path.Combine(from, "Setups", "Test.json"), ["Other"] = @"D:\elsewhere\x.json" };
			File.WriteAllText(Path.Combine(from, "Runs", "2026-01-01 00-00-00 Test", "journal.json"), record.ToJsonString());
			// a folder that holds anything is never moved into
			Directory.CreateDirectory(to);
			File.WriteAllText(Path.Combine(to, "something.txt"), "x");
			Throws<InvalidOperationException>(() => DataFolder.MoveTo(from, to), "a folder that holds files is refused");
			Check(File.Exists(Path.Combine(from, "Setups", "Test.json")), "and the data stays where it was");
			File.Delete(Path.Combine(to, "something.txt"));
			// a folder inside the data, or one that holds it, would be copied into itself and removed with it
			Throws<InvalidOperationException>(() => DataFolder.MoveTo(from, Path.Combine(from, "inside")), "a folder inside the data is refused");
			Throws<InvalidOperationException>(() => DataFolder.MoveTo(from, Path.GetDirectoryName(from)!), "a folder that holds the data is refused");
			Check(File.Exists(Path.Combine(from, "Setups", "Test.json")) && !Directory.Exists(Path.Combine(from, "inside")), "and nothing moved");
			Check(DataFolder.MoveTo(from, to) == null, "nothing is left to do by hand");
			Check(!Directory.Exists(from), "the old folder is gone");
			Check(!File.Exists(Path.Combine(to, ".renamed from.txt")), "every path was repointed: the copy carries no mark of a move to finish");
			Check(File.ReadAllText(Path.Combine(to, "Setups", "Test.json")).Contains("Test", StringComparison.Ordinal) && Directory.Exists(Path.Combine(to, "empty folder")), "every file and folder is in the new place");
			var moved = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(to, "Runs", "2026-01-01 00-00-00 Test", "journal.json")))!;
			Check(Files.SamePath(moved["SetupFile"]!.GetValue<string>(), Path.Combine(to, "Setups", "Test.json")) && moved["Other"]!.GetValue<string>() == @"D:\elsewhere\x.json",
				"a path into the data folder follows it, any other path stays");
			Check(DataFolder.Chosen(to) && !DataFolder.StandsIn(Path.GetDirectoryName(from)!, " Data"), "the next start finds no data beside the program and the new place marked as the player's choice");
			// an app-data folder that only holds data (a copy that could not write beside itself kept it there) is no choice of the
			// player's: a new copy elsewhere keeps its own folder beside it
			var other = Path.Combine(root, "other appdata");
			Directory.CreateDirectory(Path.Combine(other, "Runs"));
			Check(DataFolder.HasData(other) && !DataFolder.Chosen(other), "data alone is not the player's choice");
			// a copy of a move that was cut off before it counted (its mark names the folder it came from) is made again
			var from2 = Path.Combine(root, "beside 2", Brand.Name + " Data");
			var to2 = Path.Combine(root, "appdata 2", Brand.Name);
			Directory.CreateDirectory(Path.Combine(from2, "Setups"));
			File.WriteAllText(Path.Combine(from2, "Setups", "Two.json"), "{ \"name\": \"Two\" }");
			Directory.CreateDirectory(to2);
			File.WriteAllText(Path.Combine(to2, ".renamed from.txt"), from2);
			// (half of the data's own file, as a copy cut off in the middle of it leaves it)
			Directory.CreateDirectory(Path.Combine(to2, "Setups"));
			File.WriteAllText(Path.Combine(to2, "Setups", "Two.json"), "{ \"na");
			File.WriteAllText(Path.Combine(to2, ".copied.txt"), "Setups\\Two.json\n");
			Check(DataFolder.MoveTo(from2, to2) == null && File.ReadAllText(Path.Combine(to2, "Setups", "Two.json")).Contains("\"Two\"", StringComparison.Ordinal),
				"a half copy of the same move is replaced by the whole one");
			// a move that counted (the old folder's mark) is finished by the next start, whatever was left of it
			var old3 = Path.Combine(root, "beside 3", Brand.Name + " Data");
			var to3 = Path.Combine(root, "appdata 3", Brand.Name);
			Directory.CreateDirectory(old3);
			Directory.CreateDirectory(to3);
			File.WriteAllText(Path.Combine(old3, DataFolder.MovedMark), to3);
			Check(DataFolder.MovedTo(old3) is { } where && Files.SamePath(where, to3), "the mark names where the data went");
			DataFolder.FinishMove(old3, to3);
			Check(DataFolder.Chosen(to3) && !Directory.Exists(old3), "the new folder is the player's choice and the old one is gone");
			// the old folder's mark goes last: while a file of it is held, the mark stays for the next start to finish the move
			var old4 = Path.Combine(root, "beside 4", Brand.Name + " Data");
			var to4 = Path.Combine(root, "appdata 4", Brand.Name);
			Directory.CreateDirectory(old4);
			Directory.CreateDirectory(to4);
			File.WriteAllText(Path.Combine(old4, DataFolder.MovedMark), to4);
			File.WriteAllText(Path.Combine(old4, "held.json"), "{}");
			using (new FileStream(Path.Combine(old4, "held.json"), FileMode.Open, FileAccess.Read, FileShare.None)) {
				DataFolder.FinishMove(old4, to4);
				Check(File.Exists(Path.Combine(old4, DataFolder.MovedMark)), "a file that will not go keeps the mark beside it");
			}
			DataFolder.FinishMove(old4, to4);
			Check(!Directory.Exists(old4) && !Directory.EnumerateDirectories(Path.GetDirectoryName(old4)!).Any(), "and the next start removes the rest, the mark last, nothing left beside it");
			// the old folder leaves its data-folder name before its mark goes: what a stop at that moment leaves is no data folder
			var beside8 = Path.Combine(root, "beside 8");
			Directory.CreateDirectory(Path.Combine(beside8, Brand.Name + " Data (being removed 1a2b3c4d)"));
			Check(!DataFolder.StandsIn(beside8, " Data"), "a folder left half way through its removal is never taken for the data");
			// the copy is made beside the new place and takes its name when whole: one left by a cut-off move is this move's and
			// goes; a path that only looks as if it lies in the data ("../") is left as it was
			var from7 = Path.Combine(root, "beside 7", Brand.Name + " Data");
			var to7 = Path.Combine(root, "appdata 7", Brand.Name);
			Directory.CreateDirectory(Path.Combine(from7, "Setups"));
			File.WriteAllText(Path.Combine(from7, "Setups", "Up.json"), "{ \"mo2\": \"" + (from7 + "/../MO2").Replace("\\", "\\\\", StringComparison.Ordinal) + "\" }");
			Directory.CreateDirectory(to7 + " (being copied)");
			// (as a cut-off move leaves it: its mark names the data folder it came from first, then part of the copy)
			File.WriteAllText(Path.Combine(to7 + " (being copied)", ".renamed from.txt"), from7);
			Directory.CreateDirectory(Path.Combine(to7 + " (being copied)", "Setups"));
			File.WriteAllText(Path.Combine(to7 + " (being copied)", "Setups", "Up.json"), "{ \"mo");
			// (and its own list, which names each file before it is copied)
			File.WriteAllText(Path.Combine(to7 + " (being copied)", ".copied.txt"), "Setups\\Up.json\n");
			Check(DataFolder.MoveTo(from7, to7) == null && File.Exists(Path.Combine(to7, "Setups", "Up.json")) && !Directory.Exists(to7 + " (being copied)"),
				"a copy left by a cut-off move is replaced, and the whole copy takes the new place's name");
			Equal((from7 + "/../MO2").Replace("\\", "/", StringComparison.Ordinal),
				JsonNode.Parse(File.ReadAllText(Path.Combine(to7, "Setups", "Up.json")))!["mo2"]!.GetValue<string>().Replace("\\", "/", StringComparison.Ordinal), "a path that leaves the data folder by \"..\" stays as it was");
			// a copy of a move that never counted (its source still holds the data) is never taken for the data folder
			var parent9 = Path.Combine(root, "appdata 9");
			var source9 = Path.Combine(root, "beside 9", Brand.Name + " Data");
			Directory.CreateDirectory(Path.Combine(source9, "Setups"));
			Directory.CreateDirectory(Path.Combine(parent9, Brand.Name));
			File.WriteAllText(Path.Combine(parent9, Brand.Name, ".renamed from.txt"), source9);
			Check(Files.SamePath(DataFolder.Adopt(parent9, "", () => false), source9) && File.Exists(Path.Combine(parent9, Brand.Name, ".renamed from.txt")),
				"the data stays where it came from, and the copy keeps its mark for the move to be made again");
			// a cut-off move retried: its own staged copy goes, a file another program put in it meanwhile stays (and the move waits)
			var from13 = Path.Combine(root, "beside 13", Brand.Name + " Data");
			var to13 = Path.Combine(root, "appdata 13", Brand.Name);
			Directory.CreateDirectory(Path.Combine(from13, "Setups"));
			File.WriteAllText(Path.Combine(from13, "Setups", "S.json"), "{}");
			File.WriteAllText(Path.Combine(from13, "Setups", "T.json"), "{}");
			Directory.CreateDirectory(Path.Combine(to13 + " (being copied)", "Setups"));
			File.WriteAllText(Path.Combine(to13 + " (being copied)", ".renamed from.txt"), from13);
			File.WriteAllText(Path.Combine(to13 + " (being copied)", ".copied.txt"), "Setups\\S.json\n");
			File.WriteAllText(Path.Combine(to13 + " (being copied)", "Setups", "S.json"), "{}");
			// (another program's file, where a data file not copied yet would have gone)
			File.WriteAllText(Path.Combine(to13 + " (being copied)", "Setups", "T.json"), "theirs");
			Throws<InvalidOperationException>(() => DataFolder.MoveTo(from13, to13), "a staged copy that also holds another's file");
			Check(File.ReadAllText(Path.Combine(to13 + " (being copied)", "Setups", "T.json")) == "theirs" && !File.Exists(Path.Combine(to13 + " (being copied)", "Setups", "S.json")),
				"its own copy is gone; the other file stays, though a data file of that name was still to come");
			// a move that fails while it copies: what it copied by its own list goes, a file another program put there stays
			var from15 = Path.Combine(root, "beside 15", Brand.Name + " Data");
			var to15 = Path.Combine(root, "appdata 15", Brand.Name);
			Directory.CreateDirectory(Path.Combine(from15, "Setups"));
			File.WriteAllText(Path.Combine(from15, "Setups", "A.json"), "{ \"a\": 1 }");
			DataFolder.AfterCopy = a_staging => {
				File.WriteAllText(Path.Combine(a_staging, "theirs.txt"), "x");
				// (and a copy that will not read back whole: the move fails)
				File.WriteAllText(Path.Combine(a_staging, "Setups", "A.json"), "{ \"a\": 2 }");
			};
			try {
				Throws<IOException>(() => DataFolder.MoveTo(from15, to15), "a copy that does not read back whole");
			} finally {
				DataFolder.AfterCopy = null;
			}
			Check(File.Exists(Path.Combine(to15 + " (being copied)", "theirs.txt")) && !File.Exists(Path.Combine(to15 + " (being copied)", "Setups", "A.json")) &&
				File.Exists(Path.Combine(from15, "Setups", "A.json")), "takes back only what it copied; another's file stays, and the data is where it was");
			// another program's file at a data file's name, made the moment before that file is copied: the copy fails, and the
			// file - never on the copy's list - stays
			var from17 = Path.Combine(root, "beside 17", Brand.Name + " Data");
			var to17 = Path.Combine(root, "appdata 17", Brand.Name);
			Directory.CreateDirectory(Path.Combine(from17, "Setups"));
			File.WriteAllText(Path.Combine(from17, "Setups", "B.json"), "{}");
			DataFolder.BeforeCopy = a_target => File.WriteAllText(a_target, "theirs");
			try {
				Throws<IOException>(() => DataFolder.MoveTo(from17, to17), "a file put where a copy goes, the moment before");
			} finally {
				DataFolder.BeforeCopy = null;
			}
			Check(File.ReadAllText(Path.Combine(to17 + " (being copied)", "Setups", "B.json")) == "theirs", "that file stays as it was");
			// a cut-off move whose last copy is not on its list yet (stopped between the copy and the list): that file stays, and
			// the move waits for it - no file the list does not name is taken away
			var from18 = Path.Combine(root, "beside 18", Brand.Name + " Data");
			var to18 = Path.Combine(root, "appdata 18", Brand.Name);
			Directory.CreateDirectory(Path.Combine(from18, "Setups"));
			File.WriteAllText(Path.Combine(from18, "Setups", "C.json"), "{}");
			Directory.CreateDirectory(Path.Combine(to18 + " (being copied)", "Setups"));
			File.WriteAllText(Path.Combine(to18 + " (being copied)", ".renamed from.txt"), from18);
			File.WriteAllText(Path.Combine(to18 + " (being copied)", ".copied.txt"), "");
			File.WriteAllText(Path.Combine(to18 + " (being copied)", "Setups", "C.json"), "{}");
			Throws<InvalidOperationException>(() => DataFolder.MoveTo(from18, to18), "a cut-off copy holding a file its list does not name");
			Check(File.Exists(Path.Combine(to18 + " (being copied)", "Setups", "C.json")), "that file stays");
			// the save itself, after a move removed the old folder: refused, and the folder is not made again
			var saveData = Path.Combine(root, "beside 19", Brand.Name + " Data");
			var saveFile = Path.Combine(saveData, "Setups", "Saved.json");
			Directory.CreateDirectory(Path.GetDirectoryName(saveFile)!);
			Equal<string?>(null, ToolRunner.App.MainViewModel.WriteSetup(saveData, saveFile, "{ \"name\": \"Saved\" }", Path.Combine(saveData, "Setup backups")), "a save where the data is writes");
			Check(File.ReadAllText(saveFile).Contains("Saved", StringComparison.Ordinal), "and the file holds it");
			Directory.Delete(saveData, true);
			Check(ToolRunner.App.MainViewModel.WriteSetup(saveData, saveFile, "{ \"name\": \"Saved\" }", Path.Combine(saveData, "Setup backups"))?.Contains("no longer in", StringComparison.Ordinal) == true &&
				!Directory.Exists(saveData), "a save after the old folder is gone is refused and makes nothing again");
			// ... and one cut off after its copy took its place: the copy goes, another's file in the new place stays
			var from14 = Path.Combine(root, "beside 14", Brand.Name + " Data");
			var to14 = Path.Combine(root, "appdata 14", Brand.Name);
			Directory.CreateDirectory(Path.Combine(from14, "Setups"));
			File.WriteAllText(Path.Combine(from14, "Setups", "S.json"), "{}");
			Directory.CreateDirectory(Path.Combine(to14, "Setups"));
			File.WriteAllText(Path.Combine(to14, ".renamed from.txt"), from14);
			File.WriteAllText(Path.Combine(to14, "Setups", "S.json"), "{}");
			File.WriteAllText(Path.Combine(to14, ".copied.txt"), "Setups\\S.json\n");
			File.WriteAllText(Path.Combine(to14, "theirs.txt"), "x");
			Throws<InvalidOperationException>(() => DataFolder.MoveTo(from14, to14), "a published copy that also holds another's file");
			Check(File.Exists(Path.Combine(to14, "theirs.txt")) && !File.Exists(Path.Combine(to14, "Setups", "S.json")) && File.Exists(Path.Combine(from14, "Setups", "S.json")),
				"its own copy is gone, the other file stays, the data is where it was");
			// a "(being copied)" folder that holds someone else's files is not this move's: the move waits for it, the files stay
			var from11 = Path.Combine(root, "beside 11", Brand.Name + " Data");
			var to11 = Path.Combine(root, "appdata 11", Brand.Name);
			Directory.CreateDirectory(Path.Combine(from11, "Setups"));
			Directory.CreateDirectory(to11 + " (being copied)");
			File.WriteAllText(Path.Combine(to11 + " (being copied)", "theirs.txt"), "x");
			Throws<InvalidOperationException>(() => DataFolder.MoveTo(from11, to11), "a staging folder holding another's file is refused");
			Check(File.Exists(Path.Combine(to11 + " (being copied)", "theirs.txt")), "and that file stays");
			// the old folder that cannot be marked after the copy took its place: the copy goes again, but only what the move put
			// there - a file another program put in the new place meanwhile stays
			var from12 = Path.Combine(root, "beside 12", Brand.Name + " Data");
			var to12 = Path.Combine(root, "appdata 12", Brand.Name);
			Directory.CreateDirectory(Path.Combine(from12, "Setups"));
			File.WriteAllText(Path.Combine(from12, "Setups", "S.json"), "{}");
			DataFolder.AfterPublish = a_to => {
				File.WriteAllText(Path.Combine(a_to, "theirs.txt"), "x");
				// (the old folder's mark cannot be written: a folder stands where it would go)
				Directory.CreateDirectory(Path.Combine(from12, DataFolder.MovedMark));
			};
			try {
				Throws<IOException>(() => DataFolder.MoveTo(from12, to12), "a move whose old folder cannot be marked");
			} finally {
				DataFolder.AfterPublish = null;
			}
			Check(File.Exists(Path.Combine(to12, "theirs.txt")) && !File.Exists(Path.Combine(to12, "Setups", "S.json")) && !Directory.Exists(Path.Combine(to12, "Setups")),
				"takes back only its own copy: another's file in the new place stays");
			Check(File.Exists(Path.Combine(from12, "Setups", "S.json")), "and the data is still where it was");
			Directory.Delete(Path.Combine(from12, DataFolder.MovedMark));
			// a folder inside the data that is a link to another folder stops the move: it would be copied as what it leads to
			var from10 = Path.Combine(root, "beside 10", Brand.Name + " Data");
			var elsewhere10 = Path.Combine(root, "backups elsewhere");
			Directory.CreateDirectory(from10);
			Directory.CreateDirectory(elsewhere10);
			Junction(Path.Combine(from10, "Backups"), elsewhere10);
			Check(Directory.Exists(Path.Combine(from10, "Backups")), "the test's junction inside the data was made");
			Check(Thrown<InvalidOperationException>(() => DataFolder.MoveTo(from10, Path.Combine(root, "appdata 10", Brand.Name)), "a link inside the data").Message.Contains("is a link to another folder", StringComparison.Ordinal),
				"a link inside the data stops the move, named");
			Directory.Delete(Path.Combine(from10, "Backups"));
			// one writer of the data at a time, across copies of the program: a second hold waits for the first, then goes on
			Task second;
			using (DataFolder.HoldWrites()) {
				second = Task.Run(() => {
					using var h = DataFolder.HoldWrites();
				});
				Check(!second.Wait(300), "a second writer waits while the first holds the data");
			}
			Check(second.Wait(5000), "and goes on once the first lets go");
			// a save that waited for a move finds where the data stands once it may write: a move that removed the old folder whole
			// (its mark with it) leaves nothing to write into, and the save is refused rather than making the old folder again
			var waitedData = Path.Combine(root, "beside 16", Brand.Name + " Data");
			Directory.CreateDirectory(Path.Combine(waitedData, "Setups"));
			Equal<string?>(null, DataFolder.WhyNotWritable(waitedData), "the data where it is may be written");
			Task<string?> waitingSave;
			using (DataFolder.HoldWrites()) {
				waitingSave = Task.Run(() => {
					using var h = DataFolder.HoldWrites();
					return DataFolder.WhyNotWritable(waitedData);
				});
				Check(!waitingSave.Wait(300), "the save waits for the move");
				Directory.Delete(waitedData, true);
			}
			Check(waitingSave.Wait(5000) && waitingSave.Result?.Contains("no longer in", StringComparison.Ordinal) == true && !Directory.Exists(waitedData),
				"and, the old folder gone, is refused without making it again: " + waitingSave.Result);
			Directory.CreateDirectory(waitedData);
			File.WriteAllText(Path.Combine(waitedData, DataFolder.MovedMark), to);
			Check(DataFolder.WhyNotWritable(waitedData)?.Contains("has moved to", StringComparison.Ordinal) == true, "a folder that still holds the move mark is refused too, naming where the data went");
			// a setup written by hand (a comment, a comma after its last value, a path with forward slashes) is repointed too
			var from5 = Path.Combine(root, "beside 5", Brand.Name + " Data");
			var to5 = Path.Combine(root, "appdata 5", Brand.Name);
			Directory.CreateDirectory(Path.Combine(from5, "Setups"));
			File.WriteAllText(Path.Combine(from5, "Setups", "Hand.json"), "{\n  // written by hand\n  \"backups\": \"" + from5.Replace('\\', '/') + "/Backups\",\n}");
			Check(DataFolder.MoveTo(from5, to5) == null, "a setup written by hand moves");
			var hand = JsonNode.Parse(File.ReadAllText(Path.Combine(to5, "Setups", "Hand.json")), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
			Check(Files.SamePath(hand["backups"]!.GetValue<string>(), Path.Combine(to5, "Backups")), "and its path into the data, written with forward slashes, follows it: " + hand["backups"]);
			// a file that does not read as JSON but names the old folder stops the move: it would go on naming it after
			var from6 = Path.Combine(root, "beside 6", Brand.Name + " Data");
			var to6 = Path.Combine(root, "appdata 6", Brand.Name);
			Directory.CreateDirectory(Path.Combine(from6, "Setups"));
			File.WriteAllText(Path.Combine(from6, "Setups", "Broken.json"), "{ \"backups\": \"" + from6.Replace("\\", "\\\\", StringComparison.Ordinal) + "\\\\Backups\", ");
			Throws<IOException>(() => DataFolder.MoveTo(from6, to6), "a file naming the data folder that cannot be repointed stops the move");
			Check(File.Exists(Path.Combine(from6, "Setups", "Broken.json")) && !Directory.Exists(to6), "and the data stays where it was, nothing of the copy left");
			// a link that leads into the data folder is that folder, whatever its own path says
			var link = Path.Combine(root, "link into the data");
			Directory.CreateDirectory(Path.Combine(from5 + " again", "inside"));
			Junction(link, Path.Combine(from5 + " again", "inside"));
			// (both paths taken literally: a "%" that a command processor would read as a variable is only a letter here)
			var percent = Path.Combine(root, "100%PATH% here");
			Directory.CreateDirectory(Path.Combine(percent, "target"));
			Junction(Path.Combine(percent, "link"), Path.Combine(percent, "target"));
			Check(Files.FinalPath(Path.Combine(percent, "link")) is { } leads && Files.SamePath(leads, Path.Combine(percent, "target")), "a junction in a folder whose name holds %...% leads where it was made to");
			Directory.Delete(Path.Combine(percent, "link"));
			Check(Directory.Exists(link), "the test's junction was made");
			Throws<InvalidOperationException>(() => DataFolder.MoveTo(from5 + " again", link), "a link is refused as the new place");
			Throws<InvalidOperationException>(() => DataFolder.MoveTo(from5 + " again", Path.Combine(link, "deeper")), "a folder reached through a link into the data is refused");
			Directory.Delete(link);
			// a SUBST drive letter that stands for a folder inside the data is that folder too
			var letter = Enumerable.Range('G', 19).Select(c => (char)c).FirstOrDefault(c => !Directory.Exists(c + @":\"));
			Check(letter != default, "a free drive letter for the test");
			bool Subst(string a_args)
			{
				using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("subst.exe", a_args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
				p.WaitForExit();
				return p.ExitCode == 0;
			}
			// (taken back only when this run made it: a letter another run took meanwhile stays that run's)
			Check(Subst($"{letter}: \"{Path.Combine(from5 + " again", "inside")}\""), "the test's drive letter was made");
			try {
				Check(Directory.Exists(letter + @":\"), "the test's drive letter stands");
				Throws<InvalidOperationException>(() => DataFolder.MoveTo(from5 + " again", $@"{letter}:\deeper"), "a folder on a drive letter that stands for one inside the data is refused");
			} finally {
				Subst($"{letter}: /d");
			}
			// data reached by a network path - which may be this very PC's own folder (\\localhost\C$\...) - is never moved: how it
			// stands to the new place cannot be told (where Windows will not open that path, it is refused all the same)
			var local = Path.GetFullPath(from5 + " again");
			var viaNetwork = @"\\localhost\" + local[0] + "$" + local[2..];
			Throws<InvalidOperationException>(() => DataFolder.MoveTo(viaNetwork, Path.Combine(local, "inside", "deeper")), "data reached by a network path is not moved");
			Check(Directory.Exists(Path.Combine(local, "inside")) && !Directory.Exists(Path.Combine(local, "inside", "deeper")), "and nothing was copied");
			// Windows' own names, as GetFinalPathNameByHandle gives them, read as plain paths: a share's is a network path, which the
			// move refuses (no share opens for a test here - this PC's only shares are Windows' admin ones, refused without rights)
			Equal(@"\\server\share\Tool Runner Data", Files.PlainFinalName(@"\\?\UNC\server\share\Tool Runner Data"), "a share's final name is its network path");
			Equal(@"C:\Users\x\Tool Runner", Files.PlainFinalName(@"\\?\C:\Users\x\Tool Runner"), "a local folder's final name is its plain path");
			Equal(@"D:\plain", Files.PlainFinalName(@"D:\plain"), "a name without the prefix stays as it is");
			// the same with Windows answering for the share (no share of this PC opens for certain): the refusal is the network one
			var realName = DataFolder.FinalPathOf;
			var elsewhere = Path.Combine(root, "appdata net", Brand.Name);
			try {
				DataFolder.FinalPathOf = a_dir => Files.SamePath(a_dir, local) ? @"\\localhost\share\Tool Runner Data" : realName(a_dir);
				var fromNet = Thrown<InvalidOperationException>(() => DataFolder.MoveTo(local, elsewhere), "data whose folder Windows names on the network");
				Check(fromNet.Message.Contains("the data is in a network folder", StringComparison.Ordinal), "is refused for that: " + fromNet.Message);
				DataFolder.FinalPathOf = a_dir => Files.SamePath(a_dir, Path.GetDirectoryName(Path.GetDirectoryName(elsewhere)!)!) ? @"\\server\share" : realName(a_dir);
				var toNet = Thrown<InvalidOperationException>(() => DataFolder.MoveTo(local, elsewhere), "a new place Windows names on the network");
				Check(toNet.Message.Contains("is a network folder", StringComparison.Ordinal), "is refused for that: " + toNet.Message);
			} finally {
				DataFolder.FinalPathOf = realName;
			}
			Check(Directory.Exists(local) && !Directory.Exists(elsewhere), "and nothing was copied either way");
		} finally {
			if (Directory.Exists(root)) {
				Directory.Delete(root, true);
			}
		}
	}

	// the rename: the takeover's own check for an old copy, as the program runs it (no stand-in): a running program file under
	// the old name keeps the old folder where it is; once it ends, the folder is taken over
	static void T_DataFolderWaitsForARunningOldCopy()
	{
		var parent = Path.Combine(Path.GetTempPath(), "Tool Runner test old copy " + Guid.NewGuid().ToString("N"));
		var fake = Path.Combine(parent, "bin", Brand.OldNames[0] + ".exe");
		Directory.CreateDirectory(Path.Combine(parent, Brand.OldNames[0] + " Data", "Setups"));
		Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
		File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fake);
		using var p = Process.Start(new ProcessStartInfo(fake, "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
		try {
			Equal(Path.Combine(parent, Brand.OldNames[0] + " Data"), DataFolder.Adopt(parent, " Data"), "an old copy runs: its folder is used");
			Check(!Directory.Exists(Path.Combine(parent, Brand.Name + " Data")), "nothing moved");
			p.Kill();
			p.WaitForExit();
			// the check looks at the whole PC: another old-name program may run for a moment (a second test run beside this one)
			// - waited for up to 10 s
			var now = Path.Combine(parent, Brand.Name + " Data");
			var got = DataFolder.Adopt(parent, " Data");
			for (var i = 0; i < 20 && got != now; i++) {
				Thread.Sleep(500);
				got = DataFolder.Adopt(parent, " Data");
			}
			Equal(now, got, "it ended: the folder is taken over");
		} finally {
			if (!p.HasExited) {
				p.Kill();
			}
			p.WaitForExit();
			Directory.Delete(parent, true);
		}
	}

	// the rename: an old data folder is taken over and every path inside it repointed - escaping, letters outside English and
	// an apostrophe in the path never matter, a sibling folder sharing the name is left, a cut-off repoint is finished
	static void T_DataFolderTakenOverUnderTheNewName()
	{
		var parent = Path.Combine(Path.GetTempPath(), "Tool Runner test adopt " + Guid.NewGuid().ToString("N"), "Bob's José");
		try {
			string Old(string a_rest = "") => Path.Combine(parent, Brand.OldNames[0] + " Data") + a_rest;
			string New(string a_rest = "") => Path.Combine(parent, Brand.Name + " Data") + a_rest;
			void Write(string a_file, string a_json)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(a_file)!);
				File.WriteAllText(a_file, a_json);
			}
			var sibling = Path.Combine(parent, Brand.OldNames[0] + " Data2", "x");
			var relaxed = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
			Write(Old(@"\Setups\A.json"), new JsonObject { ["backups"] = Old(@"\Backups"), ["scratch"] = Old(), ["other"] = sibling,
				["list"] = new JsonArray(Old(@"\Runs\1")) }.ToJsonString(relaxed));
			Write(Old(@"\Runs\1\journal.json"), new JsonObject { ["folder"] = Old(@"\Runs\1") }.ToJsonString());
			Equal(Old(), DataFolder.Adopt(parent, " Data", () => true), "an old copy running: the old folder is used, nothing moved");
			Check(Directory.Exists(Old()) && !Directory.Exists(New()), "left where it was");
			Directory.CreateDirectory(New());
			Equal(New(), DataFolder.Adopt(parent, " Data", () => false), "an empty new folder does not hide the old one");
			Check(!Directory.Exists(Old()), "the old folder moved");
			var a = JsonNode.Parse(File.ReadAllText(New(@"\Setups\A.json")))!;
			Equal(New(@"\Backups"), a["backups"]!.ToString(), "a path inside (written with literal letters)");
			Equal(New(), a["scratch"]!.ToString(), "the folder itself");
			Equal(sibling, a["other"]!.ToString(), "a sibling folder sharing the name is left");
			Equal(New(@"\Runs\1"), a["list"]![0]!.ToString(), "a path in a list");
			Equal(New(@"\Runs\1"), JsonNode.Parse(File.ReadAllText(New(@"\Runs\1\journal.json")))!["folder"]!.ToString(), "a path written escaped");
			Check(!File.Exists(New(@"\.renamed from.txt")), "the repoint finished, its mark gone");
			// a repoint cut off half way: the mark makes the next start finish it
			Write(New(@"\Setups\B.json"), new JsonObject { ["backups"] = Old(@"\Backups") }.ToJsonString());
			File.WriteAllText(New(@"\.renamed from.txt"), Old());
			Equal(New(), DataFolder.Adopt(parent, " Data", () => false), "the new folder in use");
			Equal(New(@"\Backups"), JsonNode.Parse(File.ReadAllText(New(@"\Setups\B.json")))!["backups"]!.ToString(), "the cut-off repoint finished");
			// a new folder in use is never replaced by an old one that turns up beside it
			Write(Old(@"\Setups\C.json"), "{}");
			Equal(New(), DataFolder.Adopt(parent, " Data", () => false), "a new folder with files is kept");
			Check(File.Exists(Old(@"\Setups\C.json")), "the old one left alone");
		} finally {
			Directory.Delete(Path.GetDirectoryName(parent)!, true);
		}
	}

	// the rename: the run lock is held under the old name too, so an old copy of the program never runs beside this one
	static void T_RunLockCoversTheOldName()
	{
		var oldLock = "Local\\" + Brand.OldNames[0] + " run" + Runner.LockScope;
		using (var hold = Runner.HoldIfFree()) {
			Check(hold != null, "free: taken");
			var open = Mutex.TryOpenExisting(oldLock, out var m);
			m?.Dispose();
			Check(open, "the old name's lock is held too");
		}
		using var taken = new ManualResetEventSlim();
		using var done = new ManualResetEventSlim();
		var t = new Thread(() => {
			using var m = new Mutex(true, oldLock);
			taken.Set();
			done.Wait();
			m.ReleaseMutex();
		});
		t.Start();
		taken.Wait();
		try {
			using var hold = Runner.HoldIfFree();
			Check(hold == null, "an old copy holding its lock: no run");
			Check(Runner.HoldIfFree() is null, "still none");
		} finally {
			done.Set();
			t.Join();
		}
		using var after = Runner.HoldIfFree();
		Check(after != null, "free again once the old copy let go");
	}

	// the mods a LOD step builds from are found off their plugins' record groups, each with
	// its own page; the setup's own outputs (made from them) are left out
	static void T_LodGuideFindsTheModsItBuildsFrom()
	{
		using var f = new FakeMo2();
		static void WithGroups(string a_path, params string[] a_groups)
		{
			FakeMo2.Plugin(a_path, false);
			using var fs = new FileStream(a_path, FileMode.Append);
			foreach (var g in a_groups) {
				var h = new byte[24 + 8];
				Encoding.ASCII.GetBytes("GRUP").CopyTo(h, 0);
				BitConverter.GetBytes((uint)h.Length).CopyTo(h, 4);
				Encoding.ASCII.GetBytes(g).CopyTo(h, 8);
				fs.Write(h);
			}
		}
		// a top-level group holding one record (a_nested: inside one more group, as a worldspace's cells are)
		static void WithRecord(string a_path, string a_group, string a_record, uint a_flags, bool a_nested)
		{
			FakeMo2.Plugin(a_path, false);
			var rec = new byte[24 + 4];
			Encoding.ASCII.GetBytes(a_record).CopyTo(rec, 0);
			BitConverter.GetBytes(4u).CopyTo(rec, 4);
			BitConverter.GetBytes(a_flags).CopyTo(rec, 8);
			var inner = rec;
			if (a_nested) {
				inner = new byte[24 + rec.Length];
				Encoding.ASCII.GetBytes("GRUP").CopyTo(inner, 0);
				BitConverter.GetBytes((uint)inner.Length).CopyTo(inner, 4);
				Encoding.ASCII.GetBytes("CELL").CopyTo(inner, 8);
				rec.CopyTo(inner, 24);
			}
			var top = new byte[24 + inner.Length];
			Encoding.ASCII.GetBytes("GRUP").CopyTo(top, 0);
			BitConverter.GetBytes((uint)top.Length).CopyTo(top, 4);
			Encoding.ASCII.GetBytes(a_group).CopyTo(top, 8);
			inner.CopyTo(top, 24);
			using var fs = new FileStream(a_path, FileMode.Append);
			fs.Write(top);
		}
		f.Mod("Grass Mod");
		WithGroups(Path.Combine(f.Mods, "Grass Mod", "Grass.esp"), "WEAP", "GRAS");
		File.WriteAllText(Path.Combine(f.Mods, "Grass Mod", "meta.ini"), "[General]\r\ngameName=SkyrimSE\r\nmodid=12345\r\n");
		f.Mod("Tree Mod");
		WithGroups(Path.Combine(f.Mods, "Tree Mod", "Trees.esp"), "TREE");
		WithGroups(Path.Combine(f.Mods, "Output A", "OutA.esp"), "GRAS");
		f.Mod("Static Trees");
		WithRecord(Path.Combine(f.Mods, "Static Trees", "StaticTrees.esp"), "STAT", "STAT", 0x40, false);
		f.Mod("Plain Statics");
		WithRecord(Path.Combine(f.Mods, "Plain Statics", "PlainStatics.esp"), "STAT", "STAT", 0, false);
		f.Mod("Land Mod");
		WithRecord(Path.Combine(f.Mods, "Land Mod", "Land.esp"), "WRLD", "LAND", 0, true);
		f.Mod("Unticked Plugin");
		WithGroups(Path.Combine(f.Mods, "Unticked Plugin", "Off.esp"), "GRAS");
		var ml = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(ml, File.ReadAllText(ml).Replace("+Needs B", "+Grass Mod\r\n+Tree Mod\r\n+Static Trees\r\n+Plain Statics\r\n+Land Mod\r\n+Unticked Plugin\r\n+Needs B", StringComparison.Ordinal));
		// only plugins switched on count (Off.esp is installed but not ticked in plugins.txt)
		File.AppendAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "*Grass.esp\r\n*Trees.esp\r\n*StaticTrees.esp\r\n*PlainStatics.esp\r\n*Land.esp\r\nOff.esp\r\n");
		Equal(new HashSet<string> { "WEAP", "GRAS" }.SetEquals(Plugins.TopGroups(Path.Combine(f.Mods, "Grass Mod", "Grass.esp"))), true, "its record groups read off their headers");
		f.WriteSetup(("grass", new JsonObject { ["output_mod"] = "Output A" }));
		var setup = Setup.Load(f.SetupFile);
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var grass = LodGuide.Find(setup, mo2, "grass");
		Equal("Grass Mod", grass.Single().Mod, "the grass mod, not the setup's own output: " + string.Join(", ", grass.Select(h => h.Mod)));
		Equal("https://www.nexusmods.com/skyrimspecialedition/mods/12345", grass.Single().Page, "its own page");
		var dyn = LodGuide.Find(setup, mo2, "dyndolod");
		Check(dyn.Any(h => h.Mod == "Tree Mod" && h.Page == null) && dyn.Any(h => h.Mod == "Grass Mod"), "DynDOLOD: grass and trees, a mod with no page said so");
		Check(dyn.Any(h => h.Mod == "Static Trees") && dyn.All(h => h.Mod != "Plain Statics"), "a static flagged Has Tree LOD counts as a tree, a plain static does not");
		Check(grass.All(h => h.Mod != "Unticked Plugin"), "a plugin not switched on is not counted");
		Equal("Land Mod", LodGuide.Find(setup, mo2, "xlodgen").Single().Mod, "xLODGen: a landscape record inside a worldspace");
		Equal(0, LodGuide.Find(setup, mo2, "landscapefixer").Count, "not a LOD step");
	}

	// TexGen / DynDOLOD keep their window's values in no file, but write them into their debug log: learned from there, and a
	// later run that used other values is named in the run's notes
	static void T_ToolWindowValuesReadOffItsLog()
	{
		using var f = new FakeMo2();
		var exe = Path.Combine(f.Root, "Tools", "DynDOLOD", "TexGenx64.exe");
		Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(exe)!, "Logs"));
		File.WriteAllText(exe, "");
		var log = Path.Combine(Path.GetDirectoryName(exe)!, "Logs", "TexGen_SSE_Debug_log.txt");
		File.WriteAllText(log, "[00:01] [81BC] [ListVars] <Debug: GrassHDBillboardsAmbient: 80>\r\n[00:01] [81BC] [Other] <Debug: x: y>\r\n" +
			"[00:15] [81BC] [ListVars] <Debug: GrassHDBillboardsAmbient: 100>\r\n[00:15] [81BC] [ListVars] <Debug: GrassHDBillboards: True>\r\n");
		var vars = ToolSettings.ReadVars(log);
		Equal("100", vars["GrassHDBillboardsAmbient"], "the last run's value wins");
		Equal(2, vars.Count, "only ListVars lines");
		Equal(log, ToolSettings.VarsLog(exe, "texgen"), "its debug log is found beside the tool");
		Equal(null, ToolSettings.VarsLog(exe, "landscapefixer"), "a tool without one has none");
		StepRegistry.Replace(new FakeStep("texgen", (ctx, s) => {
			Thread.Sleep(50);
			File.WriteAllText(log, "[00:01] [81BC] [ListVars] <Debug: GrassHDBillboardsAmbient: 60>\r\n[00:01] [81BC] [ListVars] <Debug: GrassHDBillboards: True>\r\n");
		}));
		f.WriteSetup(("texgen", new JsonObject { ["exe"] = exe }));
		var setup = Setup.Load(f.SetupFile);
		ToolSettings.Keep(setup, "texgen", "TexGen", [], vars);
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "a different value never fails the run: " + r.Verdict);
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(j.Notes.Any(n => n.Contains("GrassHDBillboardsAmbient 60 (learned 100)", StringComparison.Ordinal)), "the difference is named: " + string.Join(" | ", j.Notes));
		Check(j.Notes.All(n => !n.Contains("GrassHDBillboards True", StringComparison.Ordinal)), "an equal value is not");
	}

	// the window looks every second: a run record is read again only when its file changed, a removed run drops out, a broken
	// record is skipped (not the whole list)
	static void T_RunRecordsAreReadAgainOnlyWhenChanged()
	{
		using var f = new FakeMo2();
		var runs = Path.Combine(f.Root, "Runs");
		Journal Make(string a_id, RunState a_state)
		{
			var j = new Journal { Id = a_id, Folder = Path.Combine(runs, a_id), State = a_state, Verdict = a_id };
			Directory.CreateDirectory(j.Folder);
			j.Save();
			return j;
		}
		Make("2026-10-01 10-00-00", RunState.Finished);
		var second = Make("2026-10-02 10-00-00", RunState.Failed);
		var cache = new JournalCache(runs);
		var a = cache.Read();
		Equal(2, a.Count, "both runs");
		Equal("2026-10-02 10-00-00", a[0].Id, "newest first");
		var b = cache.Read();
		Check(ReferenceEquals(a[0], b[0]) && ReferenceEquals(a[1], b[1]), "an unchanged record is not read again");
		second.Verdict = "changed and longer";
		second.Save();
		var c = cache.Read();
		Equal("changed and longer", c[0].Verdict, "a changed record is read again");
		Check(ReferenceEquals(a[1], c[1]), "the other one still not");
		File.WriteAllText(Path.Combine(Make("2026-10-03 10-00-00", RunState.Running).Folder, "journal.json"), "{ broken");
		Equal(2, cache.Read().Count, "a broken record is skipped, the rest kept");
		Directory.Delete(second.Folder, true);
		var d = cache.Read();
		Equal(1, d.Count, "a removed run drops out");
		Equal("2026-10-01 10-00-00", d[0].Id, "the one left");
	}

	// ------------------------------------------------------------------ the PC-level pre-checks (SystemChecks)
	static void SetLight(string a_path)
	{
		var b = File.ReadAllBytes(a_path);
		b[9] |= 0x02;
		File.WriteAllBytes(a_path, b);
	}

	static void T_PluginLimitCountsByKind()
	{
		using var f = new FakeMo2();
		f.Mod("Lights", ("Light.esl", false, []), ("LightFlag.esp", false, []), ("LightMaster.esm", true, []), ("Unticked.esp", false, []), ("Big.esp", false, []));
		SetLight(Path.Combine(f.Mods, "Lights", "LightFlag.esp"));
		SetLight(Path.Combine(f.Mods, "Lights", "LightMaster.esm"));
		FakeMo2.Plugin(Path.Combine(f.Game, "Data", "ccTest.esl"), false);
		FakeMo2.Plugin(Path.Combine(f.Game, "Data", "Update.esm"), true);
		File.WriteAllText(Path.Combine(f.Game, "Skyrim.ccc"), "ccTest.esl\r\nccAbsent.esm\r\n");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Lights\r\n" + f.Text("modlist.txt"));
		File.AppendAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "*Light.esl\r\n*LightFlag.esp\r\n*LightMaster.esm\r\nUnticked.esp\r\n*Big.esp\r\n*Gone.esp\r\n*Update.esm\r\n");
		var c = SystemChecks.CountPlugins(ProfileOf(f));
		// full: Skyrim.esm, Update.esm (once, though plugins.txt names it too), the six of the fake, Big.esp; Gone.esp is nowhere
		Equal(9, c.Full, "full plugins");
		// light: the .esl, the flagged .esp, the flagged .esm, the Creation Club .esl that Skyrim.ccc names
		Equal(4, c.Light, "light plugins");
		Check(!Plugins.IsLight(Path.Combine(f.Mods, "Lights", "Big.esp")), "an unflagged .esp is full");
		Check(Plugins.IsLight(Path.Combine(f.Mods, "Lights", "LightMaster.esm")), "an ESL-flagged .esm is light");
		Equal(0, SystemChecks.PluginLimit(c).Count(), "far from both limits");
		Check(SystemChecks.PluginLimit(new(255, 0)).Single().Blocking, "255 full plugins stop the run");
		Check(!SystemChecks.PluginLimit(new(254, 0)).Single().Blocking, "254 full plugins only warn (the outputs add more)");
		Equal(0, SystemChecks.PluginLimit(new(249, 4086)).Count(), "249 full and 4086 light: fine");
		Check(!SystemChecks.PluginLimit(new(10, 4096)).Single().Blocking, "4096 light plugins only warn");
		Check(SystemChecks.PluginLimit(new(10, 4097)).Single().Blocking, "4097 light plugins stop the run");
		var unread = SystemChecks.PluginLimit(new(250 - 10, 10, 20)).Single();
		Check(!unread.Blocking && unread.Text.Contains("could be over", StringComparison.Ordinal), "unreadable plugins are said, and what they could mean");
		// a plugin held open with no sharing cannot be read. One that has not changed since it was last read is still known (its
		// flags are remembered with its time and size); one that changed since is neither light nor full
		var locked = Path.Combine(f.Mods, "Lights", "LightFlag.esp");
		using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) {
			Equal<bool?>(true, Plugins.LightOrNot(locked), "held but unchanged since it was read: still known");
			Equal(0, SystemChecks.CountPlugins(ProfileOf(f)).Unreadable, "and counted as what it is");
		}
		File.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddSeconds(10));
		using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) {
			Equal<bool?>(null, Plugins.LightOrNot(locked), "an unreadable plugin is neither kind");
			Equal(1, SystemChecks.CountPlugins(ProfileOf(f)).Unreadable, "and counted apart");
		}
	}

	static void T_AdminOnOneSideOnly()
	{
		Equal(0, SystemChecks.Admin(false, false, false).Count(), "both normal");
		Check(SystemChecks.Admin(false, true, false).Single().Blocking, "Mod Organizer as administrator, the runner not: stop");
		Check(SystemChecks.Admin(false, false, true).Single().Blocking, "Mod Organizer set to always run as administrator: stop");
		Equal(2, SystemChecks.Admin(false, true, true).Count(), "both named");
		var self = SystemChecks.Admin(true, true, true).ToList();
		Check(self.Count == 1 && !self[0].Blocking, "the runner as administrator: one warning (whatever it starts runs as administrator too)");
	}

	static void T_ASecondMo2IsNamed()
	{
		var ours = @"C:\Games\MO2\ModOrganizer.exe";
		Equal(0, SystemChecks.OtherMo2([@"c:\games\mo2\MODORGANIZER.exe"], ours).Count(), "our own, any case");
		var i = SystemChecks.OtherMo2([ours, @"D:\Other MO2\ModOrganizer.exe", @"D:\Other MO2\ModOrganizer.exe"], ours).Single();
		Check(i.Blocking && i.Text.Contains(@"D:\Other MO2\ModOrganizer.exe", StringComparison.Ordinal), "another install stops the run, named once");
	}

	static void T_RestartBatteryAndLongPaths()
	{
		Equal(0, SystemChecks.Power(false, null).Count(), "plugged in, nothing pending");
		Check(!SystemChecks.Power(true, null).Single().Blocking, "a pending restart warns");
		var b = SystemChecks.Power(false, new(true, 40)).Single();
		Check(!b.Blocking && b.Text.Contains("40 %", StringComparison.Ordinal), "on battery warns with its level");
		Check(!SystemChecks.Power(false, new(true, 255)).Single().Text.Contains('%', StringComparison.Ordinal), "an unknown level is left out");
		var deep = Path.Combine(Path.GetTempPath(), new string('x', SystemChecks.LongFolder));
		Check(!SystemChecks.LongPaths([deep, deep, "", Path.GetTempPath()]).Single().Blocking, "a long folder warns once");
	}

	static void T_TroubleFoldersAreWarned()
	{
		SystemChecks.TroubleInput In(string a_game = @"C:\Games\Skyrim", string a_base = @"C:\Modding\MO2", string a_mods = @"C:\Modding\MO2\mods",
			string a_out = "", string a_docs = @"C:\Users\p\Documents", bool a_cfa = false, Func<string, bool>? a_net = null) =>
			new([("the game", a_game), ("Mod Organizer's instance", a_base), ("Mod Organizer's mods", a_mods), ("xlodgen's output folder", a_out)], a_docs,
				[@"C:\Program Files", @"C:\Program Files (x86)"], [@"C:\Users\p\OneDrive"], a_cfa, a_net ?? (_ => false));
		Equal(0, SystemChecks.TroubleFolders(In()).Count(), "a clean setup: nothing");
		var pf = SystemChecks.TroubleFolders(In(a_game: @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition")).Single();
		Check(!pf.Blocking && pf.Text.StartsWith("the game (", StringComparison.Ordinal) && pf.Text.Contains(@"inside C:\Program Files (x86)", StringComparison.Ordinal),
			"the game in Program Files warns");
		Equal(0, SystemChecks.TroubleFolders(In(a_game: @"C:\Program Files Extra\Skyrim")).Count(), "a folder that only starts with the same letters is not inside");
		Check(SystemChecks.TroubleFolders(In(a_docs: @"C:\Users\p\OneDrive\Documents")).Single().Text.Contains("Documents", StringComparison.Ordinal), "Documents in OneDrive warns");
		// the instance in OneDrive with its mods inside it: one warning, naming the instance only
		var one = SystemChecks.TroubleFolders(In(a_base: @"C:\Users\p\OneDrive\MO2", a_mods: @"C:\Users\p\OneDrive\MO2\mods")).Single();
		Check(one.Text.Contains("Mod Organizer's instance", StringComparison.Ordinal) && !one.Text.Contains("mods (", StringComparison.Ordinal), "one cause told once");
		Check(SystemChecks.TroubleFolders(In(a_out: @"C:\Users\p\OneDrive\LOD")).Single().Text.StartsWith("xlodgen's output folder", StringComparison.Ordinal), "a step's output in the cloud warns");
		Check(SystemChecks.TroubleFolders(In(a_game: @"C:\Users\p\OneDrive\Skyrim")).Single().Text.StartsWith("the game", StringComparison.Ordinal), "the game in the cloud warns");
		var net = SystemChecks.TroubleFolders(In(a_net: d => d.StartsWith(@"C:\Modding", StringComparison.Ordinal))).Single();
		Check(net.Text.Contains("Mod Organizer's instance (", StringComparison.Ordinal) && !net.Text.Contains("Mod Organizer's mods (", StringComparison.Ordinal) &&
			net.Text.Contains(" is on a network drive", StringComparison.Ordinal), "the network named once for the instance and the mods inside it");
		Check(SystemChecks.TroubleFolders(In(a_cfa: true)).Single().Text.Contains("controlled folder access", StringComparison.Ordinal), "controlled folder access warns");
		Check(SystemChecks.TroubleFolders(In(a_cfa: true, a_docs: @"C:\Users\p\OneDrive\Documents")).All(i => !i.Blocking), "every one only warns");
		// the real network probe on local paths
		// Windows' own folder: on the local disk on every PC (the temp folder can be set to a share)
		var local = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		Check(!SystemChecks.IsNetworkPath(local), "Windows' folder is local");
		Check(!SystemChecks.IsNetworkPath(@"\\?\" + local), "a long-form local path is local");
		Check(SystemChecks.IsNetworkPath(@"\\server\share\mods") && SystemChecks.IsNetworkPath(@"\\?\UNC\server\share\mods"), "a share is a network folder");
		Check(SystemChecks.IsNetworkPath(@"\\.\UNC\server\share\mods"), "the device form of a share is a network folder");
		Check(!SystemChecks.IsNetworkPath(@"\\?\Volume{0b1c2d3e-0000-0000-0000-100000000000}\mods"), "a volume named by its id is local");
		// the same folder under two names is named once; cloud folders are taken once, a folder inside another left out
		var twice = SystemChecks.TroubleFolders(In(a_mods: @"C:\Users\p\OneDrive\x", a_out: @"C:\Users\p\OneDrive\x\")).Single();
		Equal(1, twice.Text.Split(@"OneDrive\x").Length - 1, "one folder, one mention");
		Equal(@"C:\Users\p\Dropbox", string.Join("|", SystemChecks.Roots([@"C:\Users\p\Dropbox\", @"c:\users\p\dropbox", @"C:\Users\p\Dropbox\Sub", ""])), "the cloud roots");
		// a probe that throws leaves only its own part out (every probe stubbed: the test never reads this PC)
		var notes = new List<ToolRunner.Steps.Issue>();
		var was = SystemChecks.ControlledFolderAccess;
		var clouds = SystemChecks.CloudFolders;
		var docs = SystemChecks.Documents;
		var pfs = SystemChecks.ProgramFolders;
		try {
			SystemChecks.ControlledFolderAccess = () => throw new System.Security.SecurityException("locked down");
			SystemChecks.CloudFolders = () => throw new InvalidOperationException("a settings file of the wrong shape");
			SystemChecks.Documents = () => @"C:\Users\p\Documents";
			SystemChecks.ProgramFolders = () => [@"C:\Program Files"];
			var input = SystemChecks.ReadTrouble([("the game", @"C:\Program Files\Skyrim")], notes);
			Check(!input.ControlledAccess && input.CloudFolders.Count == 0, "the failing probes give nothing");
			Equal(2, notes.Count, "each failing probe told once");
			Check(SystemChecks.TroubleFolders(input).Single().Text.Contains("Program Files", StringComparison.Ordinal), "the probes that read still warn");
		} finally {
			SystemChecks.ControlledFolderAccess = was;
			SystemChecks.CloudFolders = clouds;
			SystemChecks.Documents = docs;
			SystemChecks.ProgramFolders = pfs;
		}
	}

	static void T_PrecheckCarriesThePcChecks()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Equal(0, Preflight.Run(setup, mo2, []).Count(i => i.Blocking), "a clean fake: nothing blocks");
		var was = SystemChecks.Mo2Paths;
		try {
			SystemChecks.Mo2Paths = () => [Path.Combine(f.Root, "Another", "ModOrganizer.exe")];
			Check(Preflight.Run(setup, mo2, []).Any(i => i.Blocking && i.Text.Contains("another Mod Organizer", StringComparison.Ordinal)), "the check reaches the pre-check");
		} finally {
			SystemChecks.Mo2Paths = was;
		}
		var clouds = SystemChecks.CloudFolders;
		try {
			SystemChecks.CloudFolders = () => [f.Root];
			Check(Preflight.Run(setup, mo2, []).Any(i => i.Text.Contains("cloud app", StringComparison.Ordinal)), "the trouble folders reach the pre-check");
		} finally {
			SystemChecks.CloudFolders = clouds;
		}
		var unread = SystemChecks.Mo2Unreadable;
		try {
			SystemChecks.Mo2Unreadable = _ => true;
			var issues = Preflight.Run(setup, mo2, []);
			Check(issues.Any(i => i.Blocking && i.Text.StartsWith(SystemChecks.UnreadableText, StringComparison.Ordinal)), "an unreadable Mod Organizer stops the run");
		} finally {
			SystemChecks.Mo2Unreadable = unread;
		}
		// Mod Organizer set to always run as administrator, through the whole check; and its probe failing leaves the others
		var flagged = SystemChecks.AdminFlagged;
		try {
			SystemChecks.AdminFlagged = _ => true;
			Check(SystemChecks.Run(mo2, ProfileOf(f), []).Single(i => i.Blocking).Text.Contains("always run as administrator", StringComparison.Ordinal), "the flag stops the run");
			SystemChecks.Mo2Unreadable = _ => throw new System.ComponentModel.Win32Exception(5);
			Check(SystemChecks.Run(mo2, ProfileOf(f), []).Any(i => i.Blocking && i.Text.Contains("always run as administrator", StringComparison.Ordinal)),
				"a failing process probe does not hide the flag");
		} finally {
			SystemChecks.AdminFlagged = flagged;
			SystemChecks.Mo2Unreadable = unread;
		}
		// a switched-off step's folder is not the run's business
		var offOut = Path.Combine(f.Root, new string('q', SystemChecks.LongFolder));
		var off = f.WriteSetup(("pandora", new JsonObject { ["output_dir"] = offOut }));
		((JsonObject)((JsonArray)off.Root["steps"]!)[0]!)["on"] = false;
		off.Save();
		Check(!Preflight.Run(Setup.Load(off.File), mo2, []).Any(i => i.Text.Contains(offOut, StringComparison.Ordinal)), "a switched-off step's output is not checked");
		// a step's output folder deep down is warned about too
		var deepOut = Path.Combine(f.Root, new string('o', SystemChecks.LongFolder));
		var withOut = f.WriteSetup(("pandora", new JsonObject { ["output_dir"] = deepOut }));
		Check(Preflight.Run(withOut, mo2, []).Any(i => i.Text.Contains(deepOut, StringComparison.Ordinal)), "a step's output folder is checked for its length");
	}

	// a blocking PC check refuses the run before anything is touched
	static void T_ASecondMo2RefusesTheRun()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		f.WriteSetup(("landscapefixer", []));
		var before = f.Text("modlist.txt");
		var was = SystemChecks.Mo2Paths;
		try {
			SystemChecks.Mo2Paths = () => [Path.Combine(f.Root, "Another MO2", "ModOrganizer.exe")];
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(r.Folder == null && r.Verdict.StartsWith("not started", StringComparison.Ordinal) && r.Verdict.Contains("another Mod Organizer", StringComparison.Ordinal), r.Verdict);
			Equal(before, f.Text("modlist.txt"), "the profile untouched");
		} finally {
			SystemChecks.Mo2Paths = was;
		}
	}

	// a player's folder can be deeper than Windows' old 260-letter limit: the profile's own files are still read, changed and put back
	static void T_LongPathProfileWorks()
	{
		using var f = new FakeMo2("\r\n", string.Join('\\', Enumerable.Repeat("a long folder name for the test", 9)));
		Check(f.ProfileDir.Length > 300, "the fake sits past 260 letters");
		var p = ProfileOf(f);
		var snap = p.Snapshot();
		p.SetMods([], ["Base Mod"]);
		Check(!p.IsOn("Base Mod"), "a mod switched off in a long path");
		p.Restore(snap);
		Check(p.IsOn("Base Mod"), "and put back");
		Equal(7, SystemChecks.CountPlugins(p).Full, "its plugins counted");
	}

	// ------------------------------------------------------------------ run again from the failed step, a queue of setups
	static Journal RunOf(RunState a_state, params (string Key, StepState State)[] a_steps) => new() {
		SetupFile = @"C:\Setups\Test.json", State = a_state, Steps = a_steps.Select(s => new StepRecord { Key = s.Key, State = s.State }).ToList(),
	};

	static void T_RetryStartsAtTheStepThatDidNotFinish()
	{
		var failed = RunOf(RunState.Failed, ("synthesis", StepState.Done), ("texgen", StepState.Failed), ("dyndolod", StepState.Skipped));
		Equal("texgen", Retry.FromStep(failed), "the failed step");
		var req = Retry.Request(failed, @"C:\Data")!;
		Equal("texgen", req.From, "from the failed step");
		Equal("dyndolod", req.Until, "to where the run ended");
		Equal<string?>(null, req.Only, "not one step only");
		Equal("dyndolod", Retry.FromStep(RunOf(RunState.Stopped, ("texgen", StepState.Done), ("dyndolod", StepState.Stopped))), "a stopped step");
		// "stop after this step": the run writes the steps it never reached as Skipped - the first of them is where it carries on
		Equal("dyndolod", Retry.FromStep(RunOf(RunState.Stopped, ("synthesis", StepState.Done), ("texgen", StepState.Done), ("dyndolod", StepState.Skipped))), "after a stop after");
		// refused before its first step (Mod Organizer would not close): every step Skipped - from the start
		Equal("texgen", Retry.FromStep(RunOf(RunState.Failed, ("texgen", StepState.Skipped), ("dyndolod", StepState.Skipped))), "nothing ran yet");
		Equal("dyndolod", Retry.FromStep(RunOf(RunState.Restored, ("texgen", StepState.Done), ("dyndolod", StepState.Running))), "put back after a crash");
		Equal<string?>(null, Retry.FromStep(RunOf(RunState.Finished, ("texgen", StepState.Done))), "a finished run has nothing to run again");
		Equal<string?>(null, Retry.FromStep(RunOf(RunState.Running, ("texgen", StepState.Running))), "a run not finished is put back first");
		Equal<string?>(null, Retry.FromStep(RunOf(RunState.Failed, ("texgen", StepState.Done))), "every step done");
	}

	static void T_RetryRunsOnlyWhatIsLeft()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()), ("playsave", new JsonObject()));
		var j = RunOf(RunState.Failed, ("pandora", StepState.Done), ("playsave", StepState.Failed));
		j.SetupFile = setup.File;
		var req = Retry.Request(j, f.Data)!;
		Equal("playsave", string.Join(",", Runner.Plan(setup, req.Only, req.From, req.Until).Select(s => s.Key)), "the plan of a run again holds only the steps left");
	}

	static void T_QueueRunsInOrderAndEndsWhereItShould()
	{
		using var f = new FakeMo2();
		var data = f.Data;
		var files = new[] { "A", "B", "C" }.Select(n => Path.Combine(data, "Setups", n + ".json")).ToList();
		List<string> Ran(Dictionary<string, RunState> a_results, bool a_keep, out List<QueueRun.Item> a_items)
		{
			var ran = new List<string>();
			a_items = QueueRun.Run(files, data, a_keep, r => {
				var name = Path.GetFileNameWithoutExtension(r.SetupFile);
				ran.Add(name);
				var st = QueueRun.Read(data);
				Check(st != null && st.Setups[st.Current] == r.SetupFile, "the queue file names the setup that runs");
				Check(r.From == null && r.Only == null, "each setup runs whole");
				// nothing else may start between or during the queue's runs: the lock is the queue's for its whole length
				var other = Task.Run(Runner.HoldIfFree).GetAwaiter().GetResult();
				Check(other == null, "the run lock is held by the queue");
				return new RunOutcome(a_results.TryGetValue(name, out var s) ? s : RunState.Finished, name + " verdict", "x");
			});
			Check(!File.Exists(QueueRun.File(data)), "the queue file is gone at the end");
			return ran;
		}
		Equal("A,B,C", string.Join(",", Ran([], false, out var all)), "all three in order");
		Check(all.All(i => i.Outcome?.State == RunState.Finished), "each one finished");
		Equal("A,B", string.Join(",", Ran(new() { ["B"] = RunState.Failed }, false, out var stopped)), "a failed run ends the queue");
		Check(QueueRun.Summary(stopped).Last() == "C: not run", "the setup never run is said");
		Check(File.ReadAllText(QueueRun.LastFile(data)).Contains("C: not run", StringComparison.Ordinal), "the last queue's summary is kept for the window");
		Equal("A,B,C", string.Join(",", Ran(new() { ["B"] = RunState.Failed }, true, out _)), "keep going goes past a failed run");
		Equal("A", string.Join(",", Ran(new() { ["A"] = RunState.Stopped }, true, out _)), "a stopped run always ends the queue");
		Equal<QueueRun.State?>(null, QueueRun.Read(data), "no queue read once it ended");
	}

	// the template's rules for a theme, held for every theme the program ships (more themes, each one checked):
	// text and its quieter kind readable on every panel, the outlined and the primary button's text readable, the three states
	// readable and apart, borders visible
	static void T_EveryShippedThemeKeepsTheRules()
	{
		static double Lum(string a_hex)
		{
			var c = Convert.FromHexString(a_hex.TrimStart('#')[^6..]);
			static double Lin(byte a_v) => a_v / 255.0 <= 0.03928 ? a_v / 255.0 / 12.92 : Math.Pow((a_v / 255.0 + 0.055) / 1.055, 2.4);
			return 0.2126 * Lin(c[0]) + 0.7152 * Lin(c[1]) + 0.0722 * Lin(c[2]);
		}
		static double Ratio(string a_a, string a_b) => (Math.Max(Lum(a_a), Lum(a_b)) + 0.05) / (Math.Min(Lum(a_a), Lum(a_b)) + 0.05);
		static double Hue(string a_hex)
		{
			var c = Convert.FromHexString(a_hex.TrimStart('#')[^6..]);
			double r = c[0] / 255.0, g = c[1] / 255.0, b = c[2] / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
			if (max - min < 1e-9) {
				return 0;
			}
			var h = max == r ? (g - b) / (max - min) % 6 : max == g ? (b - r) / (max - min) + 2 : (r - g) / (max - min) + 4;
			return (h * 60 + 360) % 360;
		}
		var names = ToolRunner.App.Theme.ShippedNames.Prepend(ToolRunner.App.Theme.Default).ToList();
		Check(names.Count >= 10 && names.Distinct().Count() == names.Count, "a real collection of themes, each named once");
		var light = 0;
		foreach (var name in names) {
			var t = System.Text.Json.Nodes.JsonNode.Parse(ToolRunner.App.Theme.ShippedText(name))!;
			Equal(name, t["name"]!["value"]!.ToString(), "the theme's own name is its file's");
			string C(string a_key) => t["colors"]![a_key]!["value"]!.ToString();
			void Need(string a_what, string a_a, string a_b, double a_least) =>
				Check(Ratio(a_a, a_b) >= a_least, $"{name}: {a_what} is {Ratio(a_a, a_b):0.00} : 1, needs {a_least:0.0} ({a_a} on {a_b})");
			var mode = t["windowsMode"]!["value"]!.ToString();
			Check(mode is "Light" or "Dark", name + ": its mode");
			light += mode == "Light" ? 1 : 0;
			Equal(mode == "Dark", Lum(C("Bg")) < 0.2, name + ": its mode matches its background");
			foreach (var back in new[] { "Bg", "Panel", "Panel2" }) {
				Need("text on " + back, C("Text"), C(back), 4.5);
				Need("quiet text on " + back, C("Muted"), C(back), 4.5);
			}
			foreach (var back in new[] { "Panel2", "HoverFill", "Panel" }) {
				Need("an outlined button's text on " + back, C("AccentLight"), C(back), 4.5);
			}
			foreach (var back in new[] { "Panel", "Bg" }) {
				Need("a heading on " + back, C("Accent"), C(back), 3.0);
				Need("a border on " + back, C("Line"), C(back), 1.25);
			}
			var stops = t["gradients"]!["PrimaryAndProgress"]!["stops"]!.AsArray();
			Check(stops.Count >= 2, name + ": the primary gradient has its stops");
			foreach (var stop in stops) {
				Need("the primary button's text on its gradient", C("OnAccent"), stop!["color"]!.ToString(), 4.5);
			}
			Need("a tick on a ticked box", C("OnAccent"), C("Accent"), 4.5);
			foreach (var state in new[] { "Good", "Warn", "Bad" }) {
				Need(state + " on a panel", C(state), C("Panel"), 4.5);
				Need(state + " inside a box", C(state), C("Panel2"), 4.5);
			}
			foreach (var fill in new[] { "BannerBad", "GuidedFill", "HoverFill" }) {
				Need("text on " + fill, C("Text"), C(fill), 4.5);
			}
			foreach (var (a, b) in new[] { ("Good", "Warn"), ("Warn", "Bad"), ("Good", "Bad") }) {
				var d = Math.Abs(Hue(C(a)) - Hue(C(b)));
				Check(Math.Min(d, 360 - d) >= 25, $"{name}: {a} and {b} are told apart by hue too");
			}
			var accent = t["windowsAccent"]!.AsObject();
			Check(new[] { "Light3", "Light2", "Light1", "Base", "Dark1", "Dark2", "Dark3" }.All(k => accent[k] != null), name + ": Windows' own controls get all seven accent shades");
		}
		Check(light >= 3 && names.Count - light >= 3, "light and dark themes both");
		// Color-blind safe changes only the three states, over the default: each still readable there
		foreach (var state in new[] { "#0072B2", "#7A5C00", "#B34E00" }) {
			Check(Ratio(state, "#FFFFFF") >= 4.5 && Ratio(state, "#F3F6FA") >= 4.5, "a color-blind safe state is readable on the default's panels: " + state);
		}
		// a new theme started from an existing one: the whole template with that theme's values, under a free name, its lines of where kept
		var made = ToolRunner.App.Theme.CopyAs("Midnight", null);
		try {
			var copy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(made))!;
			Equal("#0B1220", copy["colors"]!["Bg"]!["value"]!.ToString(), "the copy has the theme's colors");
			Check(copy["colors"]!["Bg"]!["where"]!.ToString().Length > 10 && copy["fonts"]!["Ui"]!["value"]!.ToString().Length > 0, "and the template's guidance and the values the theme left alone");
			Equal("Dark", copy["windowsMode"]!["value"]!.ToString(), "and its mode");
			Equal("Midnight copy", copy["name"]!["value"]!.ToString(), "under a name of its own");
			var second = ToolRunner.App.Theme.CopyAs("Midnight", null);
			try {
				Check(!string.Equals(second, made, StringComparison.OrdinalIgnoreCase), "a second copy never overwrites the first");
			} finally {
				File.Delete(second);
			}
			Check(ToolRunner.App.Theme.List().Any(x => x.File != null && string.Equals(x.File, made, StringComparison.OrdinalIgnoreCase) && x.Name == "Midnight copy"), "and it is listed as a theme of the player's");
		} finally {
			File.Delete(made);
		}
	}

	// the window's pictures read the way the window reads them: an Icon.ico WPF could not decode crashed the program at start
	// - a picture like that fails here, never on a player's screen. Covered: every picture compiled into the program
	// and every picture App.xaml's resources make; NOT a picture a window names by its own address (use the Logo resource way)
	static void T_EveryWindowPictureLoads()
	{
		string[] kinds = [".png", ".ico", ".jpg", ".jpeg", ".bmp", ".gif"];
		var read = new List<string>();
		Exception? failed = null;
		// every pixel copied out, past the size in its header (a PNG merely cut short is NOT refused - WPF draws what it has,
		// measured - so that is no crash and not what this catches)
		static void Whole(System.Windows.Media.Imaging.BitmapSource a_b, string a_what)
		{
			Check(a_b.PixelWidth > 0 && a_b.PixelHeight > 0, a_what + " has a size");
			var stride = (a_b.PixelWidth * a_b.Format.BitsPerPixel + 7) / 8;
			a_b.CopyPixels(new byte[stride * a_b.PixelHeight], stride, 0);
		}
		var t = new Thread(() => {
			try {
				var asm = typeof(ToolRunner.App.App).Assembly;
				// each picture compiled into the program
				using (var s = asm.GetManifestResourceStream(asm.GetName().Name + ".g.resources") ?? throw new InvalidOperationException("the program has no resources")) {
					using var r = new System.Resources.ResourceReader(s);
					foreach (System.Collections.DictionaryEntry e in r) {
						var name = (string)e.Key;
						if (kinds.Any(k => name.EndsWith(k, StringComparison.OrdinalIgnoreCase)) && e.Value is Stream pic) {
							using (pic) {
								foreach (var frame in System.Windows.Media.Imaging.BitmapDecoder.Create(pic, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
									System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames) {
									Whole(frame, name);
								}
							}
							read.Add(name);
						}
					}
				}
				// the program's own resources made as its window makes them (App.xaml names its own assembly in each picture's
				// address, so they read the same from here), each picture among them read. A process holds one WPF Application:
				// this test makes it, so no other test may
				Check(System.Windows.Application.Current == null, "no WPF Application made before this test (only one per process)");
				var app = new ToolRunner.App.App();
				app.InitializeComponent();
				// the gold accent brushes over the Fluent theme's (Windows' accent, blue on most PCs): both sets load, and the one for
				// the mode in use is last, so it wins
				Check(app.Resources.MergedDictionaries.Any(d => d.Contains("TextControlElevationBorderFocusedBrush")), "the accent brushes load with App.xaml");
				ToolRunner.App.Theme.Apply(ToolRunner.App.Theme.Default, false);
				Equal("AccentLight.xaml", ToolRunner.App.Theme.AccentInUse, "the light set after the built-in look");
				var ours = app.Resources.MergedDictionaries.Last(d => d.Source?.OriginalString.EndsWith("/AccentLight.xaml", StringComparison.Ordinal) == true);
				Check(ReferenceEquals(app.Resources["TextControlElevationBorderFocusedBrush"], ours["TextControlElevationBorderFocusedBrush"]),
					"the text box's focus line is the program's own brush, not the Fluent theme's");
				// the default look is App.xaml's own values, and they are its theme file's: the gallery's picture of the default and a
				// theme started from it show what the window shows
				var daylight = System.Text.Json.Nodes.JsonNode.Parse(ToolRunner.App.Theme.ShippedText(ToolRunner.App.Theme.Default))!;
				foreach (var (key, node) in daylight["colors"]!.AsObject()) {
					var resource = key switch { "Accent" or "AccentLight" or "AccentDim" => key, _ => key } + "Color";
					Equal(((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(node!["value"]!.ToString())).ToString(),
						((System.Windows.Media.Color)app.Resources[resource]).ToString(), $"App.xaml's {resource} is the default theme file's {key}");
				}
				// every shipped theme applies whole (no value left as the default's by a misread), takes its own mode and accent set, and
				// the default comes back as it was
				foreach (var name in ToolRunner.App.Theme.ShippedNames) {
					var problems = ToolRunner.App.Theme.Apply(name, false);
					Check(problems.Count == 0, $"the theme {name} applies with nothing left out: {string.Join("; ", problems)}");
					Equal(name, ToolRunner.App.Theme.Current, "the theme in use");
					var file = System.Text.Json.Nodes.JsonNode.Parse(ToolRunner.App.Theme.ShippedText(name))!;
					Equal(file["windowsMode"]!["value"]!.ToString(), ToolRunner.App.Theme.Mode, $"{name}'s mode");
					Equal(ToolRunner.App.Theme.Mode == "Light" ? "AccentLight.xaml" : "Accent.xaml", ToolRunner.App.Theme.AccentInUse, $"{name}'s accent set");
					Equal(((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(file["colors"]!["Bg"]!["value"]!.ToString())).ToString(),
						((System.Windows.Media.Color)app.Resources["BgColor"]).ToString(), $"{name}'s background reached the window");
				}
				Check(ToolRunner.App.Theme.Apply(ToolRunner.App.Theme.ColorBlind, false).Count == 0 && ToolRunner.App.Theme.Apply(ToolRunner.App.Theme.HighContrast, false).Count == 0,
					"the two looks built in code apply");
				ToolRunner.App.Theme.Apply(ToolRunner.App.Theme.Default, false);
				Equal("#FFE9EEF5", ((System.Windows.Media.Color)app.Resources["BgColor"]).ToString(), "the default is back after the others");
				Equal("Light", ToolRunner.App.Theme.Mode, "and its mode");
				// each theme's card in the gallery reads its own file, never the look in use
				var midnight = ToolRunner.App.Theme.LookOf("Midnight", null);
				Check(midnight.Dark && midnight.Problem == null && midnight.Bg.ToString() == "#FF0B1220" && midnight.About.Length > 0, "the gallery reads a theme without applying it");
				foreach (var k in app.Resources.Keys) {
					if (app.Resources[k] is System.Windows.Media.Imaging.BitmapSource b) {
						Whole(b, $"the {k} picture");
						read.Add(k.ToString() ?? "");
					}
				}
			} catch (Exception e) when (e is not OutOfMemoryException) {
				failed = e;
			}
		});
		t.SetApartmentState(ApartmentState.STA);
		t.Start();
		t.Join();
		if (failed != null) {
			throw new InvalidOperationException(failed.Message, failed);
		}
		Check(read.Contains("Logo") && read.Any(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)), "the header logo and its file are among them: " + string.Join(", ", read));
	}

	// a start with no window of its own that hits an error ends with exit 1 and its error in crash.txt - no window and no box of
	// it ever shows (a crash box of a start with no window would sit on the screen and be waited on forever). The real program,
	// built beside the tests, is started on a hidden desktop so even a broken build never reaches the player's screen; its
	// leak guard watches the player's screen too
	static void T_AWindowlessErrorShowsNothing()
	{
		Check(File.Exists(Path.Combine(AppContext.BaseDirectory, Brand.Name + ".exe")), "the program sits beside the tests");
		// a copy of the built program in a scratch folder of this test's own: its data folder (and the crash.txt it writes) is the
		// copy's, removed after - nothing is left in a folder that outlives the test
		// never under the temp folder: a program there keeps its data in local app data (AppPaths), so its crash.txt would be elsewhere
		var scratch = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!, "Tool Runner test windowless " + Guid.NewGuid().ToString("N"));
		var exe = Path.Combine(scratch, Brand.Name + ".exe");
		var crash = Path.Combine(scratch, Brand.Name + " Data", "crash.txt");
		var keep = HiddenDesktop.GuardPoll;
		HiddenDesktop.GuardPoll = TimeSpan.FromMilliseconds(50);
		try {
			// the copy inside the cleanup's reach: a copy that fails part way leaves nothing behind
			foreach (var f in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)) {
				var rel = Path.GetRelativePath(AppContext.BaseDirectory, f);
				if (!rel.StartsWith(Brand.Name + " Data", StringComparison.OrdinalIgnoreCase)) {
					Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(scratch, rel))!);
					File.Copy(f, Path.Combine(scratch, rel));
				}
			}
			// its own data folder beside it before it starts: an app-data folder this PC chose for every copy never takes it
			Directory.CreateDirectory(Path.Combine(scratch, Brand.Name + " Data"));
			using var desk = new HiddenDesktop("Tool-Runner-test-windowless");
			// a folder Windows refuses to make: the snapshot's first step throws
			using var p = desk.Start($"\"{exe}\" --snapshot \"{Path.Combine(Path.GetTempPath(), "bad<name>")}\"", AppContext.BaseDirectory);
			// each window named the moment it is seen (gone by the time a message is written); one with no size is Windows' own
			// input helper (UAC_InputIndicatorOverlayWnd, 0x0 - measured), nothing a player can see
			var shown = new Dictionary<nint, string>();
			var sw = Stopwatch.StartNew();
			while (!p.HasExited && sw.Elapsed < TimeSpan.FromSeconds(30)) {
				foreach (var h in desk.Windows()) {
					if (!shown.ContainsKey(h) && Native.IsWindowVisible(h) && Native.GetWindowThreadProcessId(h, out var pid) != 0 && pid == p.Id &&
					    Native.GetWindowRect(h, out var r) && r.Right > r.Left && r.Bottom > r.Top) {
						var inside = string.Join(" / ", Native.ChildWindows(h).Select(Native.WindowText).Where(t => t.Length > 0));
						shown[h] = $"{Native.ClassName(h)} '{Native.WindowText(h)}' {r.Right - r.Left}x{r.Bottom - r.Top} [{inside}]";
					}
				}
				Thread.Sleep(20);
			}
			if (!p.HasExited) {
				Procs.KillTree(p.Id);
			}
			Check(desk.Leak == null, "it showed on the player's screen: " + desk.Leak);
			Check(shown.Count == 0, "it showed " + string.Join("; ", shown.Values));
			Check(sw.Elapsed < TimeSpan.FromSeconds(30), "it was still running after 30 s");
			Equal(1, p.ExitCode, "its exit code");
			Check(File.Exists(crash) && File.ReadAllText(crash).Contains("bad<name>", StringComparison.Ordinal), "its error was written in crash.txt");
		} finally {
			HiddenDesktop.GuardPoll = keep;
			// the copy's files are free once it ended (a moment after its exit)
			for (var i = 0; i < 20 && Directory.Exists(scratch); i++) {
				try {
					Directory.Delete(scratch, true);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Thread.Sleep(250);
				}
			}
		}
	}

	// ------------------------------------------------------------------ a run never fights the player
	// a failed run left its outputs sitting empty in Mod Organizer: an output this tool made that holds only its meta.ini leaves the
	// mod list and its folder moves into the run's backup; one with files, or a mod the tool did not make, stays
	static void T_AnEmptyOutputLeavesTheModList()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		p.EnsureMod("Empty Out");
		p.EnsureMod("Full Out");
		File.WriteAllText(Path.Combine(f.Mods, "Full Out", "Built.esp"), "x");
		f.Mod("Their Empty");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "-Their Empty\r\n" + f.Text("modlist.txt"));
		var park = Path.Combine(f.Backups, "run");
		Check(p.TakeOutEmptyOutput("Empty Out", park), "the empty output was taken out");
		Check(!p.TakeOutEmptyOutput("Full Out", park), "an output with files stays");
		Check(!p.TakeOutEmptyOutput("Their Empty", park), "a mod the tool did not make stays, empty or not");
		var list = f.Text("modlist.txt");
		Check(!list.Contains("Empty Out", StringComparison.Ordinal), "its mod-list line is gone");
		Check(list.Contains("-Full Out", StringComparison.Ordinal) && list.Contains("-Their Empty", StringComparison.Ordinal), "the others keep their lines");
		Check(!Directory.Exists(Path.Combine(f.Mods, "Empty Out")), "its folder left the mods folder");
		Check(File.Exists(Path.Combine(park, "empty outputs", "Empty Out", "meta.ini")), "its folder is in the run's backup, never erased");
		Check(!p.Added.Contains("Empty Out") && p.Added.Contains("Full Out"), "it is no longer kept as an added mod");
		// a folder that cannot move (a file of it held open) keeps its line too - never a folder Mod Organizer would list as new;
		// and a take-out cut off after its folder moved finishes by taking out the line that was left
		p.EnsureMod("Held Out");
		using (new FileStream(Path.Combine(f.Mods, "Held Out", "meta.ini"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
			var held = false;
			try {
				p.TakeOutEmptyOutput("Held Out", park);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				held = true;
			}
			Check(held, "a folder that cannot move says so");
		}
		Check(Directory.Exists(Path.Combine(f.Mods, "Held Out")) && f.Text("modlist.txt").Contains("-Held Out", StringComparison.Ordinal),
			"the held folder and its line are as they were");
		Directory.CreateDirectory(Path.Combine(park, "empty outputs"));
		Directory.Move(Path.Combine(f.Mods, "Held Out"), Path.Combine(park, "empty outputs", "Held Out"));
		Check(p.TakeOutEmptyOutput("Held Out", park), "the line a cut-off take-out left is taken out");
		Check(!f.Text("modlist.txt").Contains("Held Out", StringComparison.Ordinal), "no line of a folder that is gone");
	}

	// the run never closes Mod Organizer while the player is working in it: in front AND touched within the quiet time
	static void T_PlayerUsingModOrganizerIsSeen()
	{
		int[] mo2 = [500, 600];
		Check(Mo2Control.PlayerUsing(mo2, 600, TimeSpan.FromSeconds(5)), "in front and just touched = in use");
		Check(!Mo2Control.PlayerUsing(mo2, 700, TimeSpan.FromSeconds(5)), "another program in front = not in use");
		Check(!Mo2Control.PlayerUsing(mo2, 600, Mo2Control.PlayerQuiet + TimeSpan.FromSeconds(1)), "in front but left alone = not in use");
		Check(!Mo2Control.PlayerUsing([], 0, TimeSpan.Zero), "no Mod Organizer = not in use");
	}

	// every window's frame is painted in the theme's colors: Windows reads a color as 0x00BBGGRR
	static void T_WindowFrameColorsAreInWindowsOrder()
	{
		Equal(0x00332211, ToolRunner.App.WindowFrame.Ref(System.Windows.Media.Color.FromRgb(0x11, 0x22, 0x33)), "the color Windows is handed");
		Equal(0x00FFFFFF, ToolRunner.App.WindowFrame.Ref(System.Windows.Media.Colors.White), "white");
	}

	// A run never fights the player: what the player ticks, unticks or installs
	// while a run goes on is told apart from the run's own switches (and from a step's switch-and-put-back), so the put-back
	// keeps it instead of undoing it
	static void T_ThePlayersChangesDuringARunAreTold()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		p.WatchForOthers();
		p.SetMods([], ["Output A"]);
		var was = p.OursNow();
		var snap = p.Snapshot();
		p.SetMods([], ["Output B"]);
		p.Restore(snap);
		p.OursBack(was);
		Equal(0, p.ChangedByOthers().Count, "the run's own switches and a step's put-back are not the player's");
		f.Mod("Players New");
		var list = f.Text("modlist.txt").Replace("+Base Mod", "-Base Mod");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Players New\r\n" + list);
		var theirs = p.ChangedByOthers();
		Equal(2, theirs.Count, "two changes of the player's");
		Check(theirs.TryGetValue("Base Mod", out var b) && b == '-', "the mod the player unticked");
		Check(theirs.TryGetValue("Players New", out var n) && n == '+', "the mod the player installed and ticked");
		Check(!theirs.ContainsKey("Output A"), "the run's own switch is not the player's");
		Check(Profile.StatesOf(snap).ContainsKey("Base Mod") && !Profile.StatesOf(snap).ContainsKey("Players New"), "a snapshot's states read back");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "-Folder Mod Organizer Found\r\n" + f.Text("modlist.txt"));
		Check(!p.ChangedByOthers().ContainsKey("Folder Mod Organizer Found"), "a folder Mod Organizer lists as off by itself is not the player's change");
	}

	// only Landscape Fixer - a setup still naming SnowFixer or AutoBlend is told so in plain words
	static void T_OnlyLandscapeFixerIsSupported()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("snowfixer", []), ("autoblend", []));
		var p = s.Problems();
		Check(p.Count(x => x.Contains("Landscape Fixer replaced it", StringComparison.Ordinal)) == 2, "both old steps named: " + string.Join(" | ", p));
		Check(s.NotKnown.Count == 0, "not left out as a step this version does not have: they are told about plainly");
		Check(!StepCatalog.Known("snowfixer") && !StepCatalog.Known("autoblend") && StepCatalog.Known("landscapefixer"), "only Landscape Fixer is a step");
		Equal("Testing MO2.json", LandscapeFixerStep.InstanceFile(@"C:\Modding\Testing MO2\ModOrganizer.ini"), "its settings file is named after the instance's folder");
	}

	static void T_LauncherToolsAreToldThisRunsProfile()
	{
		var had = "{ \"Mo2ProfileName\": \"Another\", \"OutputLocation\": \"E:\\\\Other\", \"UiTheme\": \"system\" }";
		var at = System.Text.Json.Nodes.JsonNode.Parse(ToolRunner.Steps.LauncherStep.PointedAt(had, @"C:\M", "Test", @"C:\G", @"C:\M\mods\Out"))!;
		Equal("Test", (string?)at["Mo2ProfileName"], "this run's profile");
		Equal(@"C:\M\mods\Out", (string?)at["OutputLocation"], "this run's output");
		Equal("system", (string?)at["UiTheme"], "the player's other settings stay");
		Equal("Test", (string?)System.Text.Json.Nodes.JsonNode.Parse(ToolRunner.Steps.LauncherStep.PointedAt(null, @"C:\M", "Test", @"C:\G", @"C:\O"))!["Mo2ProfileName"], "a tool never run before");
		// Its author's word (Landscape Fixer): its settings file is no stable contract. A file it wrote that no longer
		// holds a field this program points it with (renamed in a newer version) is not written over: the step stops, names
		// the field, and says the fault is Tool Runner's to report - never the tool author's
		Equal<string?>(null, LauncherStep.MissingField("{ \"ModManager\": 1, \"Mo2InstancePath\": \"C:\\\\M\", \"Mo2ProfileName\": \"A\", \"GameLocation\": \"C:\\\\G\", \"OutputLocation\": \"C:\\\\O\" }"), "all five fields there");
		// the field that tells it the mods are Mod Organizer's is one too, and the stop says what it is for
		const string noManager = "{ \"Mo2InstancePath\": \"C:\\\\M\", \"Mo2ProfileName\": \"A\", \"GameLocation\": \"C:\\\\G\", \"OutputLocation\": \"C:\\\\O\" }";
		Equal("ModManager", LauncherStep.MissingField(noManager), "a file without the mod manager's field is found");
		Check(LauncherStep.SettingsProblem(noManager, "Landscape Fixer")?.Contains("\"ModManager\", which Tool Runner sets to tell it the mods are in Mod Organizer", StringComparison.Ordinal) == true,
			"and the stop names what that field is for");
		Equal<string?>(null, LauncherStep.MissingField(null), "no file yet: a new one gets them all");
		Equal("Mo2InstancePath", LauncherStep.MissingField("{}"), "a file the tool wrote with no fields holds none of them");
		Equal("Mo2InstancePath", LauncherStep.MissingField("[1, 2]"), "a file that holds no settings object holds none of them");
		Throws<System.Text.Json.JsonException>(() => LauncherStep.MissingField("{ not settings"), "a file that does not read is said, never taken as complete");
		Equal("OutputLocation", LauncherStep.MissingField("{ \"Mo2InstancePath\": \"C:\\\\M\", \"Mo2ProfileName\": \"A\", \"GameLocation\": \"C:\\\\G\", \"OutputFolder\": \"C:\\\\O\" }"), "a renamed field is found");
		// the game's folder is one of them
		Equal("GameLocation", LauncherStep.MissingField("{ \"Mo2InstancePath\": \"C:\\\\M\", \"Mo2ProfileName\": \"A\", \"GameDirectory\": \"C:\\\\G\", \"OutputLocation\": \"C:\\\\O\" }"), "the game's folder under another name is found too");
		Check(LauncherStep.OursToFix("Landscape Fixer").Contains("Tool Runner's GitHub page, never on Landscape Fixer's own page", StringComparison.Ordinal), "and the report goes to Tool Runner");
		// what the step itself stops on, before anything is changed: a file that is there but blank stops it as well
		Equal<string?>(null, LauncherStep.SettingsProblem(null, "Landscape Fixer"), "no settings file yet: the step goes on");
		foreach (var blank in new[] { "", "   ", "\r\n\t \n" }) {
			Check(LauncherStep.SettingsProblem(blank, "Landscape Fixer")?.Contains("settings file is empty", StringComparison.Ordinal) == true, $"a blank settings file stops the step ({blank.Length} characters)");
			Equal("Mo2InstancePath", LauncherStep.MissingField(blank), "and holds none of the fields");
		}
		Check(LauncherStep.SettingsProblem("{ not settings", "Landscape Fixer")?.Contains("does not read as settings", StringComparison.Ordinal) == true, "a file that does not read stops it");
		Check(LauncherStep.SettingsProblem("{}", "Landscape Fixer")?.Contains("no longer holds \"Mo2InstancePath\"", StringComparison.Ordinal) == true, "a file that lost a field stops it");
	}

	static void T_SynthesisUsesTheGameModOrganizerRuns()
	{
		var at = SynthesisStep.WithDataFolder("{ \"DataPathOverride\": null, \"a\": 1 }", @"C:\Game's Copy\Data");
		Check(at.Contains(@"C:\\Game", StringComparison.Ordinal) && !at.Contains("null", StringComparison.Ordinal), "the folder written, its backslashes escaped");
		var own = "{ \"DataPathOverride\": \"E:\\\\Mine\" }";
		Equal(own, SynthesisStep.WithDataFolder(own, @"C:\Other"), "a data folder the player set stays");
	}

	// ------------------------------------------------------------------ each step's last run
	static StepRecord Ran(string a_key, StepState a_state, DateTime? a_started, int a_minutes = 5, string a_note = "") => new() {
		Key = a_key, Name = StepCatalog.Name(a_key), State = a_state, Started = a_started, Ended = a_started?.AddMinutes(a_minutes), Note = a_note,
	};

	static void T_LastRunsFindEachStepsLastRunAndLastFinish()
	{
		var t = new DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Local);
		const string setup = @"C:\Setups\Test.json";
		var older = new Journal { SetupFile = setup, Started = t, Folder = "older", Steps = [Ran("texgen", StepState.Done, t), Ran("dyndolod", StepState.Done, t.AddMinutes(6))] };
		var newer = new Journal { SetupFile = setup.ToUpperInvariant(), Started = t.AddDays(1), Folder = "newer",
			Steps = [Ran("texgen", StepState.Failed, t.AddDays(1), 2, "it said no"), Ran("dyndolod", StepState.Skipped, null), Ran("pandora", StepState.Pending, null)] };
		var other = new Journal { SetupFile = @"C:\Setups\Other.json", Started = t.AddDays(2), Steps = [Ran("pandora", StepState.Done, t.AddDays(2))] };
		// the order runs come in does not matter
		var of = LastRuns.Of([older, other, newer], setup);
		Equal(StepState.Failed, of["texgen"].Latest?.State, "texgen's last run is the newer one that failed");
		Equal("older", of["texgen"].LastDone?.RunFolder, "and it last finished in the older run");
		Equal("older", of["dyndolod"].Latest?.RunFolder, "a step the newer run never reached keeps its older run as its last");
		Check(!of.ContainsKey("pandora"), "a step that never started in this setup has no last run (another setup's run is not its)");
		var failed = new LastRuns.Section("texgen", of["texgen"].Latest, of["texgen"].LastDone, []);
		Check(failed.Current && !failed.OutOfDate, "no change since it last finished: its output is current, even after a failed run");
		Check(LastRuns.Line(failed).Contains("failed", StringComparison.Ordinal) && LastRuns.Line(failed).Contains("last finished", StringComparison.Ordinal) &&
		      LastRuns.Line(failed).EndsWith(" - current", StringComparison.Ordinal), "the line: failed last time, finished before, current: " + LastRuns.Line(failed));
		var detail = string.Join("\n", LastRuns.Detail(failed, "Test", ["one line it wrote"]));
		Check(detail.Contains("it said no", StringComparison.Ordinal) && detail.Contains("its output is from then", StringComparison.Ordinal) &&
		      detail.Contains("What it did:\n  one line it wrote", StringComparison.Ordinal), "the card names the failure, the finished run and what it did: " + detail);
		var never = new LastRuns.Section("pandora", null, null, null);
		Equal("never run", LastRuns.Line(never), "a step that never ran");
		var unfinished = new LastRuns.Section("texgen", of["texgen"].Latest, null, null);
		Check(LastRuns.Line(unfinished).EndsWith("it has not finished yet", StringComparison.Ordinal) && !unfinished.Current && !unfinished.OutOfDate,
			"a step that never finished is neither current nor out of date");
	}

	static void T_LastRunsCountOnlyChangesMadeOutsideInTheProfile()
	{
		var since = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Local);
		Mo2Changes.Change Row(int a_min, string a_who, string a_mod, string a_what, params string[] a_profiles) =>
			new(since.AddMinutes(a_min), a_who, a_mod, a_what, a_profiles);
		var rows = new[] {
			Row(-1, Mo2Changes.Outside, "Before", "ticked", "Test"),
			Row(1, Mo2Changes.Outside, "Grass Mod", "added (ticked, under Grass)", "test", "Other"),
			Row(2, "run of Tuesday", "By The Run", "unticked", "Test"),
			Row(3, Mo2Changes.Outside, "Elsewhere", "ticked", "Other"),
			Row(4, Mo2Changes.Outside, "Test", "profile added", "Test"),
			Row(5, Mo2Changes.Outside, "DynDOLOD Output", "unticked", "Test"),
			Row(6, Mo2Changes.Outside, "Tree Mod", "moved within Trees", "Test"),
			Row(7, Mo2Changes.Outside, "Looked At", "ticked", "Test"),
			Row(8, Mo2Changes.Outside, "Looked At", "unticked", "Test"),
		};
		var watched = since.AddDays(-1);
		var changes = LastRuns.ChangesSince(rows, [("New Mod", "removed (was under Misc)")], "Test", since, watched, m => m == "DynDOLOD Output")!;
		Equal("Grass Mod,Tree Mod,New Mod", string.Join(",", changes.Select(c => c.Mod)),
			"only changes made outside the runner, in this profile (any case), after it finished, not the setup's own mods, not one undone again - then the ones not kept yet");
		Equal("Looked At ticked", string.Join(",", LastRuns.ChangesSince(rows.Take(8), [], "Test", since, watched, _ => false)!.Where(c => c.Mod == "Looked At").Select(c => c.Mod + " " + c.What)),
			"a tick not undone still counts");
		Equal("1 added, 1 moved, 1 removed", LastRuns.Counted(changes), "counted by kind");
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.ChangesSince(rows, null, "Test", since, watched, _ => false), "a mod list that could not be read now: not known, never \"current\"");
		// the history's edges: watched only since after it finished, or the kept rows cut short after it
		// finished - not known; a mod removed and installed again is a new copy (it counts), one installed and removed again is not
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.ChangesSince(rows, [], "Test", since, since.AddMinutes(1), _ => false),
			"Mod Organizer watched only since after it finished: not known");
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.ChangesSince(rows, [], "Test", since, null, _ => false), "never watched: not known");
		var full = Enumerable.Range(0, Mo2Changes.KeepRows).Select(i => Row(10 + i, Mo2Changes.Outside, "Busy Mod", i % 2 == 0 ? "ticked" : "unticked", "Test")).ToList();
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.ChangesSince(full, [], "Test", since, watched, _ => false), "the kept rows cut short after it finished: not known");
		Equal(0, LastRuns.ChangesSince(full, [], "Test", since.AddSeconds(19 * 60 + 30), watched, _ => false)!.Count,
			"kept rows that still reach back past it are judged (an even run of ticks and unticks: back as it was)");
		var installs = new[] {
			Row(9, Mo2Changes.Outside, "Reinstalled", "removed (was under Misc)", "Test"),
			Row(10, Mo2Changes.Outside, "Reinstalled", "added (ticked, under Misc)", "Test"),
			Row(11, Mo2Changes.Outside, "Tried Out", "added (ticked, under Misc)", "Test"),
			Row(12, Mo2Changes.Outside, "Tried Out", "removed (was under Misc)", "Test"),
		};
		Equal("Reinstalled,Reinstalled", string.Join(",", LastRuns.ChangesSince(installs, [], "Test", since, watched, _ => false)!.Select(c => c.Mod)),
			"removed and installed again counts (its files may differ); installed and removed again does not");
		var t = since.AddHours(-1);
		var done = new LastRuns.Ran(t, "run", "DynDOLOD", StepState.Done, t, t.AddMinutes(24), "");
		var stale = new LastRuns.Section("dyndolod", done, done, changes);
		Check(stale.OutOfDate && LastRuns.Line(stale).EndsWith("3 changes in Mod Organizer since", StringComparison.Ordinal), "out of date: " + LastRuns.Line(stale));
		var card = string.Join("\n", LastRuns.Detail(stale, "Test", []));
		Check(card.Contains("Out of date: 3 changes in the profile Test since it finished (1 added, 1 moved, 1 removed), e.g. Grass Mod added", StringComparison.Ordinal) &&
		      card.Contains("Run it again", StringComparison.Ordinal) && !card.Contains("What it did", StringComparison.Ordinal), "the card says what changed: " + card);
		Check(LastRuns.Line(stale with { ChangedSince = null }).EndsWith("changes since: not known", StringComparison.Ordinal), "unknown is said as unknown");
		// its last finished run built into another mod than the setup names now: out of date, whatever changed
		var moved = new LastRuns.Section("dyndolod", done with { Output = "Old Output" }, done with { Output = "Old Output" }, [], "New Output");
		Check(moved.OutOfDate && !moved.Current && moved.BuiltElsewhere == "Old Output", "built into another mod than the setup names: out of date");
		Check(LastRuns.Line(moved).EndsWith("it built into Old Output then; the setup now names New Output", StringComparison.Ordinal), "the line says where: " + LastRuns.Line(moved));
		Check(string.Join("\n", LastRuns.Detail(moved, "Test", [])).Contains("Run it again to build there", StringComparison.Ordinal), "the card says what to do");
		Check(new LastRuns.Section("dyndolod", done, done, [], "New Output").Current, "a run from before outputs were recorded is judged by its changes only");
	}

	static void T_LastRunsReadTheStepsOwnLinesOfItsLog()
	{
		string[] log = [
			"21:00:00  === Synthesis ===",
			"21:00:01  synthesis said hello",
			"21:01:00  === Synthesis done in 1.0 min ===",
			"21:01:01  ",
			"21:01:02  === TexGen ===",
			"21:01:02  ",
			"21:01:03    a detail line nobody needs here",
			"21:01:04  ! a warning it gave",
			"21:02:00  made 1,234 textures",
			"21:06:00  === TexGen done in 5.0 min ===",
			"21:06:01  === DynDOLOD ===",
			"21:06:02  dyndolod line",
		];
		Equal("! a warning it gave|made 1,234 textures|=== TexGen done in 5.0 min ===", string.Join("|", LastRuns.LogLines(log, "TexGen")),
			"its own lines from its start to its done line, no detail lines, nothing of the steps around it");
		Equal("made 1,234 textures|=== TexGen done in 5.0 min ===", string.Join("|", LastRuns.LogLines(log, "TexGen", 2)), "at most the last few");
		string[] failed = ["10:00:00  === DynDOLOD ===", "10:00:05  starting", "10:01:00  !!! DynDOLOD FAILED: out of memory", "10:01:01  the run stopped"];
		Equal("starting|!!! DynDOLOD FAILED: out of memory", string.Join("|", LastRuns.LogLines(failed, "DynDOLOD")), "a failed step ends at its FAILED line");
		Equal(0, LastRuns.LogLines(log, "Pandora").Count, "a step the log never names has no lines");
	}

	static void T_LastRunsOwnModsAreTheSetupsOutputs()
	{
		var setup = Setup.FromJson(new JsonObject {
			["lodOutputs"] = new JsonArray("Old LOD"),
			["steps"] = new JsonArray(
				new JsonObject { ["step"] = "texgen", ["settings"] = new JsonObject { ["output_mod"] = "TexGen Output", ["replaces"] = new JsonArray("Older TexGen") } },
				new JsonObject { ["step"] = "shaders", ["settings"] = new JsonObject { ["caches"] = new JsonArray(
					new JsonObject { ["shader_mod"] = "Shader Mod", ["output_mod"] = "Shader Cache", ["settings_mod"] = "Shader Settings" }) } }),
		});
		var own = LastRuns.OwnMods(setup);
		Check(own.SetEquals(["Old LOD", "TexGen Output", "Older TexGen", "Shader Cache", "Shader Settings"]), "outputs, what they replace, the LOD outputs, each cache and its settings: " + string.Join(", ", own));
		Check(own.Contains("texgen output") && !own.Contains("Shader Mod"), "names in any case; the shader mod itself is the player's (its change matters)");
	}

	// the whole section off the disk: a run record, Mod Organizer's looks, the profile changed after the last look (read only)
	static void T_LastRunsBuildOffTheDisk()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("texgen", new JsonObject { ["output_mod"] = "Output A" }), ("dyndolod", new JsonObject()));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var t = DateTime.Now.AddMinutes(-30);
		var j = new Journal { SetupFile = setup.File, Started = t, Ended = t.AddMinutes(6), State = RunState.Finished, Folder = Path.Combine(Runner.RunsDir(f.Data), "2026-11-02 21-00-00 Test"),
			Steps = [Ran("texgen", StepState.Done, t)] };
		Directory.CreateDirectory(j.Folder);
		j.Save();
		File.WriteAllLines(Path.Combine(j.Folder, "log.txt"), [$"{t:HH:mm:ss}  === TexGen ===", $"{t:HH:mm:ss}  made the textures", $"{t:HH:mm:ss}  === TexGen done in 5.0 min ==="]);
		var never = LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data);
		Equal<IReadOnlyList<(string, string)>?>(null, never.Single(s => s.Key == "texgen").ChangedSince, "Mod Organizer never looked at: not known");
		Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen").ChangedSince,
			"watched only since after it finished: not known (what changed before the first look is not on record)");
		// its next run finishes while Mod Organizer is watched
		var t2 = DateTime.Now;
		j.Started = t2;
		j.Steps = [Ran("texgen", StepState.Done, t2, 0)];
		j.Save();
		var looked = LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data);
		var texgen = looked.Single(s => s.Key == "texgen");
		Check(texgen.Current, "nothing changed since the look: current");
		Equal<LastRuns.Ran?>(null, looked.Single(s => s.Key == "dyndolod").Latest, "a step that never ran");
		Equal("made the textures|=== TexGen done in 5.0 min ===", string.Join("|", LastRuns.LogOf(texgen)), "what it did, off its run's log");
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		var before = File.ReadAllText(list);
		File.WriteAllText(list, before.Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal).Replace("+Output A", "-Output A", StringComparison.Ordinal));
		var pending = LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen");
		Equal("Base Mod unticked", string.Join(",", pending.ChangedSince!.Select(c => c.Mod + " " + c.What)), "a change not looked at yet counts; the setup's own output does not");
		Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		var kept = LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen");
		Equal("Base Mod unticked", string.Join(",", kept.ChangedSince!.Select(c => c.Mod + " " + c.What)), "once kept, it still counts (once)");
		j.Steps[0].Output = "Old Output";
		j.Save();
		var elsewhere = LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen");
		Check(elsewhere.BuiltElsewhere == "Old Output" && elsewhere.OutputNow == "Output A" && elsewhere.OutOfDate, "the output its run recorded against the setup's now");
		j.Steps[0].Output = null;
		j.Save();
		// a kept history that does not read, or one set aside and started over since the step finished: not known
		var changesDir = Mo2Changes.Dir(f.Data, mo2);
		var changes = Path.Combine(changesDir, "changes.json");
		var good = File.ReadAllText(changes);
		File.WriteAllText(changes, "{ not json");
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen").ChangedSince,
			"an unreadable history: not known");
		File.WriteAllText(changes, good);
		File.WriteAllText(Path.Combine(changesDir, $"changes unreadable {DateTime.Now.AddMinutes(1):yyyy-MM-dd HH-mm-ss}.json"), "x");
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen").ChangedSince,
			"a history started over after the step finished: not known");
		Equal(before.Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal).Replace("+Output A", "-Output A", StringComparison.Ordinal), File.ReadAllText(list),
			"the profile is only read");
		var logs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
		Equal(string.Join("|", LastRuns.LogOf(texgen)), string.Join("|", LastRuns.LogOf(texgen, logs)), "the same lines when each log is read once");
		Equal(1, logs.Count, "and the log is kept for the next step of that run");
		// a run going (or one that stopped before its last look) switches mods itself: what changed since the look is not known
		var going = new Journal { SetupFile = setup.File, Started = DateTime.Now, State = RunState.Running, Folder = Path.Combine(Runner.RunsDir(f.Data), "2026-11-02 22-00-00 Test") };
		Directory.CreateDirectory(going.Folder);
		going.Save();
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen").ChangedSince,
			"not known while a run is unfinished");
	}

	static void T_ToolErrorsSayWhatAKnownErrorMeans()
	{
		var mem = ToolErrors.Explain("dyndolod", "DynDOLOD: EOutOfMemory: OUT OF MEMORY while reading textures");
		Check(mem != null && mem.What.Contains("memory", StringComparison.Ordinal) && mem.Help == "https://dyndolod.info/FAQ", "a sheson tool's out of memory, any letter case, with its FAQ");
		Check(ToolErrors.Explain("synthesis", "Out of memory") == null, "a tool's own error is only matched for that tool");
		Check(ToolErrors.Explain("texgen", "the tool crashed") == null, "an error no page names keeps the tool's own words");
		var master = ToolErrors.Explain("landscapefixer", "Landscape.esp: Missing Master Skyrim2.esm");
		Check(master != null && master.Help == null, "a missing master is known for every step");
		var lines = ToolErrors.Simple("DynDOLOD", StepState.Failed, "Out of memory", mem);
		Equal(4, lines.Count, "what, what to do, help, the tool's words: " + string.Join(" | ", lines));
		Check(lines[0] == "DynDOLOD did not finish: " + mem!.What && lines[1].StartsWith("What to do: ", StringComparison.Ordinal) &&
			lines[2] == "Help: https://dyndolod.info/FAQ" && lines[3] == "The tool's own words: Out of memory", "the lines in order");
		var plain = ToolErrors.Simple("TexGen", StepState.Failed, "the tool crashed", null);
		Check(plain.Count == 2 && plain[0] == "TexGen did not finish: the tool crashed" && plain[1].Contains("detailed log", StringComparison.Ordinal), "an unknown error points to the detailed log");
		Equal("TexGen was stopped.", ToolErrors.Simple("TexGen", StepState.Stopped, "cancelled", mem).Single(), "a stop is only a stop");
	}

	static void T_RunLogKeepsASimpleLogBesideTheDetailedOne()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner test simple log " + Guid.NewGuid().ToString("N"));
		try {
			using (var log = new RunLog(dir)) {
				log.Say("the detailed line");
				log.Detail("a detail");
				log.Simple("the simple line");
			}
			using (var marks = new RunLog(Path.Combine(dir, "marks"))) {
				marks.Simple("a warning", Level.Warn);
				marks.Simple("a failure", Level.Error);
			}
			var markLines = File.ReadAllLines(Path.Combine(dir, "marks", RunLog.SimpleName));
			Check(markLines.Length == 2 && markLines[0].EndsWith("  ! a warning", StringComparison.Ordinal) && markLines[1].EndsWith("  !!! a failure", StringComparison.Ordinal),
				"the simple log marks a warning and a failure as the detailed log does: " + string.Join(" | ", markLines));
			var simple = File.ReadAllLines(Path.Combine(dir, RunLog.SimpleName));
			var detailed = File.ReadAllText(Path.Combine(dir, "log.txt"));
			Check(simple.Length == 1 && System.Text.RegularExpressions.Regex.IsMatch(simple[0], @"^\d\d:\d\d  the simple line$"), "the simple log holds only its own line, with the time: " + string.Join(" | ", simple));
			Check(detailed.Contains("the detailed line", StringComparison.Ordinal) && !detailed.Contains("the simple line", StringComparison.Ordinal), "the detailed log keeps its own lines");
			Equal(Path.Combine(dir, RunLog.SimpleName), ToolRunner.App.MainViewModel.LogFile(dir, false), "the window shows the simple log by default");
			Equal(Path.Combine(dir, "log.txt"), ToolRunner.App.MainViewModel.LogFile(dir, true), "and the detailed one when ticked");
			File.Delete(Path.Combine(dir, RunLog.SimpleName));
			Equal(Path.Combine(dir, "log.txt"), ToolRunner.App.MainViewModel.LogFile(dir, false), "a run older than the simple log shows its detailed log");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// A long run's log as the window takes it: lines added a few at a time, the oldest cut away past 200,000 characters (as the
	// window does), every line one colored paragraph. What it keeps must be exactly the newest lines, and the time it takes is
	// the test's own time in the log (no limit here but a generous one: a busy PC is no failure)
	static void T_ALongLogIsAddedAndCutWhole()
	{
		Exception? failed = null;
		var t = new Thread(() => {
			try {
				var box = new ToolRunner.App.LogView();
				var added = 0;
				var sw = Stopwatch.StartNew();
				for (var batch = 0; batch < 3000; ++batch) {
					var lines = string.Join("\n", Enumerable.Range(0, 3).Select(i => $"21:{batch % 60:00}:{i:00}  {(i == 1 ? "! " : i == 2 && batch % 50 == 0 ? "!!! " : "")}line {batch * 3 + i} of a long run's log, about as long as most")) + "\n";
					box.AppendLines(lines);
					added += lines.Length;
					if (added > 200_000) {
						box.KeepLast(150_000);
						added = box.Length;
					}
				}
				var took = sw.Elapsed;
				var text = box.Text;
				Check(text.Length <= 200_000 && text.EndsWith("line 8999 of a long run's log, about as long as most\n", StringComparison.Ordinal), $"the newest lines are kept ({text.Length} characters)");
				Check(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == box.Document.Blocks.Count, "one paragraph for each line kept");
				Check(text.StartsWith("21:", StringComparison.Ordinal), "the cut falls between whole lines");
				Check(took < TimeSpan.FromSeconds(60), $"9,000 lines added in {took.TotalSeconds:0.0} s");
				// a newest line longer than the whole budget: every line before it goes, and it stays whole
				box.AppendLines(new string('x', 160_000) + "\n");
				box.KeepLast(150_000);
				Check(box.Document.Blocks.Count == 1 && box.Text.TrimEnd('\n').Length == 160_000, $"a line over the budget is kept alone and whole ({box.Document.Blocks.Count} lines, {box.Length} characters)");
				Console.WriteLine($"      9,000 lines added and cut in {took.TotalMilliseconds:0} ms");
			} catch (Exception e) {
				failed = e;
			}
		});
		t.SetApartmentState(ApartmentState.STA);
		t.Start();
		t.Join();
		if (failed != null) {
			throw failed;
		}
	}

	// the log's colors: green as planned, yellow a warning, red a failure or what goes off plan - read off the marks both logs write
	static void T_TheLogIsColoredByWhatEachLineSays()
	{
		Equal("Good", ToolRunner.App.LogView.BrushKey("21:00:01  TexGen started"), "a line as planned (detailed)");
		Equal("Good", ToolRunner.App.LogView.BrushKey("21:00:01    a detail"), "a detail");
		Equal("Good", ToolRunner.App.LogView.BrushKey("21:00  1/3 TexGen started"), "a line as planned (simple)");
		Equal("Warn", ToolRunner.App.LogView.BrushKey("21:00:02  ! a box is open - the game's notice"), "a warning (detailed)");
		Equal("Warn", ToolRunner.App.LogView.BrushKey("21:00  ! Note: something worth a look"), "a warning (simple)");
		Equal("Bad", ToolRunner.App.LogView.BrushKey("21:00:03  !!! the step failed"), "a failure (detailed)");
		Equal("Bad", ToolRunner.App.LogView.BrushKey("21:00  !!! The game shows a message box this run cannot answer"), "what goes off plan (simple)");
		Equal("Good", ToolRunner.App.LogView.BrushKey("21:00  a line that says ! somewhere"), "a mark only counts right after the time");
		Equal("Text", ToolRunner.App.LogView.BrushKey(""), "an empty line has no color of its own");
	}

	static void T_FailedRunsSimpleLogSaysWhatToDo()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		StepRegistry.Replace(new FakeStep("pgpatcher", (ctx, s) => throw new StepFailedException("PGPatcher.esp requires master Gone.esm")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("pgpatcher", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "failed");
		var marked = File.ReadAllLines(Path.Combine(r.Folder!, RunLog.SimpleName)).Select(l => l[7..]).ToList();
		// (the lines of a failure carry the failure's mark, as in the detailed log - the window colors them red)
		var simple = marked.Select(l => l.StartsWith("!!! ", StringComparison.Ordinal) ? l[4..] : l.StartsWith("! ", StringComparison.Ordinal) ? l[2..] : l).ToList();
		var all = string.Join(" | ", marked);
		Check(marked.Any(l => l == "!!! PGPatcher did not finish: A plugin needs a master plugin that is not loaded.") && marked[^1].StartsWith("!!! Failed: ", StringComparison.Ordinal),
			"the failure and the verdict are marked as failures: " + all);
		Check(marked[0].Contains("started: 2 step(s)", StringComparison.Ordinal) && !marked[0].StartsWith('!'), "a line as planned has no mark: " + all);
		Check(simple[0].Contains("started: 2 step(s)", StringComparison.Ordinal), "the run's start: " + all);
		Check(simple.Any(l => l.StartsWith("1/2 ", StringComparison.Ordinal) && l.EndsWith(" started", StringComparison.Ordinal)) &&
			simple.Any(l => l == "2/2 PGPatcher started"), "each step's start, numbered: " + all);
		Check(simple.Any(l => l.Contains(" finished in ", StringComparison.Ordinal)), "the first step's end: " + all);
		Check(simple.Any(l => l == "PGPatcher did not finish: A plugin needs a master plugin that is not loaded."), "the failure in plain words: " + all);
		Check(simple.Any(l => l.StartsWith("What to do: Install and tick the missing master", StringComparison.Ordinal)), "what to do: " + all);
		Check(simple.Any(l => l == "The tool's own words: PGPatcher.esp requires master Gone.esm"), "the tool's own words: " + all);
		Check(simple[^1].StartsWith("Failed: stopped at PGPatcher", StringComparison.Ordinal), "the verdict last: " + all);
		// the summary above the verdict: its title and why the run ended in red, what to do in yellow, its other rows as planned
		Check(marked.Any(l => l.StartsWith("!!! Stopped by a problem after ", StringComparison.Ordinal)) && marked.Any(l => l.StartsWith("!!!   Why: PGPatcher", StringComparison.Ordinal)),
			"the summary says how the run ended in red: " + all);
		Check(marked.Any(l => l.StartsWith("!   Next: ", StringComparison.Ordinal)) && marked.Any(l => l.StartsWith("  Steps: ", StringComparison.Ordinal)),
			"what comes next in yellow, the steps as planned: " + all);
		var detailed = File.ReadAllText(Path.Combine(r.Folder!, "log.txt"));
		Check(detailed.Contains("! what it means: A plugin needs a master", StringComparison.Ordinal), "the detailed log says what it means too");
	}

	// ------------------------------------------------------------------ Synthesis' and Landscape Fixer's own files
	// Synthesis is pointed at the game MO2 runs in every profile whose data folder is unset - null or left out - while a folder the
	// player set is kept, and its type marker stays first (Synthesis's reader needs it there)
	static void T_SynthesisDataFolderIsSetWhereverItIsUnset()
	{
		var text = """
			{ "Version": 2, "Profiles": [
			  { "$type": "SynthesisProfile", "Nickname": "A", "DataPathOverride": null },
			  { "$type": "SynthesisProfile", "Nickname": "B" },
			  { "$type": "SynthesisProfile", "Nickname": "C", "DataPathOverride": "E:\\Theirs\\Data" } ] }
			""";
		var set = JsonNode.Parse(SynthesisStep.WithDataFolder(text, "G:\\Game\\Data"))!;
		Equal("G:\\Game\\Data", set["Profiles"]?[0]?["DataPathOverride"]?.ToString(), "a null one is set");
		Equal("G:\\Game\\Data", set["Profiles"]?[1]?["DataPathOverride"]?.ToString(), "a left-out one is set");
		Equal("E:\\Theirs\\Data", set["Profiles"]?[2]?["DataPathOverride"]?.ToString(), "the player's own is kept");
		Equal("$type", (set["Profiles"]?[1] as JsonObject)?.First().Key, "the type marker stays first");
		var done = SynthesisStep.WithDataFolder(SynthesisStep.WithDataFolder(text, "G:\\Game\\Data"), "G:\\Game\\Data");
		Equal(SynthesisStep.WithDataFolder(text, "G:\\Game\\Data"), done, "a second pass changes nothing");
	}

	// a translation file of another shape (a list, a plain value, "run" not an object) is passed over, never stopping the step
	static void T_LandscapeFixerSkipsOddTranslationFiles()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner test odd translations " + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try {
			File.WriteAllText(Path.Combine(dir, "list.json"), "[1, 2]");
			File.WriteAllText(Path.Combine(dir, "value.json"), "\"just text\"");
			File.WriteAllText(Path.Combine(dir, "run.json"), "{ \"run\": [\"Generate\"] }");
			File.WriteAllText(Path.Combine(dir, "de.json"), "{ \"run\": { \"generate\": \"Erzeugen\" } }");
			Equal("Erzeugen, Generate, Start", string.Join(", ", LauncherStep.StartButtonNames(dir, ["Generate", "Start"])), "the good file is read, the odd ones passed over");
		} finally {
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// ------------------------------------------------------------------ boxes, the window's own work, the checks before a run
	// the boxes Mod Organizer shows while closing are answered: its quit question and notices by their words (whole words, so a
	// player's box about "disclosure" is not one), and its one-button notice in any language; another box of the player's is left
	static void T_Mo2CloseBoxesAreKnownByWordsOrOneButton()
	{
		Check(Mo2Control.CloseBox("Are you sure you want to quit?"), "the quit question");
		Check(Mo2Control.CloseBox("Mod Organizer is closing"), "a word's other forms count");
		Check(Mo2Control.CloseBox("Fehler beim Speichern", 1), "a one-button notice in another language");
		Check(!Mo2Control.CloseBox("Read the disclosure first", 2), "a word inside another word is not one");
		Check(!Mo2Control.CloseBox("Install this mod now?", 2), "the player's own question is left alone");
	}

	// Landscape Fixer's start button is found by its own words in every language it ships (its translation files beside it), so a
	// player who runs it in German is understood too; a broken file or no folder falls back to the English names
	static void T_LandscapeFixerKnowsItsButtonInEveryLanguage()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner test translations " + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try {
			File.WriteAllText(Path.Combine(dir, "en.json"), "{ \"run\": { \"generate\": \"Generate\" } }");
			File.WriteAllText(Path.Combine(dir, "de.json"), "{ \"run\": { \"generate\": \"Generieren\" } }");
			File.WriteAllText(Path.Combine(dir, "xx.json"), "{ not json");
			File.WriteAllText(Path.Combine(dir, "yy.json"), "{ \"run\": { \"generate\": 5 } }");
			var names = ToolRunner.Steps.LauncherStep.StartButtonNames(dir, ["Generate", "Start"]);
			Check(names.Contains("Generieren") && names.Contains("Generate") && names.Contains("Start"), "every language's word: " + string.Join(", ", names));
			Equal(3, names.Count, "each name once, a broken or odd file left out");
			Equal("Generate|Start", string.Join("|", ToolRunner.Steps.LauncherStep.StartButtonNames(Path.Combine(dir, "gone"), ["Generate", "Start"])), "no folder: the English names");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// a tool Mod Organizer never started: the lines Mod Organizer logged after the request that name the tool, or are its
	// warnings / errors, go into the failure (never the older part of its log)
	static void T_Mo2LogLinesSinceTheRequestAreNamed()
	{
		var file = Path.Combine(Path.GetTempPath(), "Tool Runner test mo2 log " + Guid.NewGuid().ToString("N") + ".log");
		try {
			File.WriteAllText(file, "12:00:00.000 [W] an old warning\nLandscapeFixer old line\n");
			var from = new FileInfo(file).Length;
			File.AppendAllText(file, "12:01:00.000 [I] loading plugins\n12:01:02.000 [E] failed to spawn \"LandscapeFixer.exe\": access denied\n12:01:03.000 [I] ready\n");
			var lines = Mo2Control.LogLinesSince(file, from, "LandscapeFixer", "LandscapeFixer.exe");
			Equal(1, lines.Count, "only the new line that matters: " + string.Join(" | ", lines));
			Check(lines[0].Contains("access denied", StringComparison.Ordinal), "the reason itself");
			Equal(2, Mo2Control.LogLinesSince(file, new FileInfo(file).Length + 100, "LandscapeFixer", "LandscapeFixer.exe").Count(l => l.Contains("LandscapeFixer", StringComparison.Ordinal)),
				"a log started again since the request is read whole");
			Equal(0, Mo2Control.LogLinesSince(file + ".gone", 0, "x", "x.exe").Count, "no log, no lines");
		} finally {
			File.Delete(file);
		}
	}

	// a screen reader hears each step whole in words (its glyph and color say it to the eye), and list rows by
	// their words - never a type's name or a file path
	static void T_ScreenReadersHearEachStepInWords()
	{
		Equal("done in 4m", ToolRunner.App.A11y.StepStatus(true, false, StepState.Done, "4m", "5m", ""), "a finished step");
		Equal("running for 2m", ToolRunner.App.A11y.StepStatus(true, false, StepState.Running, "2m", "", ""), "the running step");
		Equal("failed, PGPatcher.esp requires master Gone.esm", ToolRunner.App.A11y.StepStatus(true, false, StepState.Failed, "1m", "", "PGPatcher.esp requires master Gone.esm"), "a failure with its reason");
		Equal("switched off, usually takes 24m", ToolRunner.App.A11y.StepStatus(false, false, null, "", "24m", ""), "a switched-off step");
		Equal("left out for a new game", ToolRunner.App.A11y.StepStatus(true, true, null, "", "", ""), "a save step on a new game");
		Equal("usually takes 7m, " + ToolRunner.App.MainViewModel.NoDevBench, ToolRunner.App.A11y.StepStatus(true, false, null, "", "7m", "", false, ToolRunner.App.MainViewModel.NoDevBench),
			"a step DevBench is missing for says so to a screen reader too");
		Equal("My Setup", new ToolRunner.App.SetupItem("My Setup", @"C:\Data\Setups\My Setup.json").ToString(), "a setup is read by its name, not its path");
		var row = new ToolRunner.App.HistoryRow("Tue Oct 27  21:02", "My Setup", "1h 41m", "finished: 12 step(s)", true, @"C:\Runs\1").ToString();
		Check(row.Contains("finished", StringComparison.Ordinal) && !row.Contains(@"C:\Runs", StringComparison.Ordinal), "a past run in words: " + row);
		var said = new List<(string, bool)>();
		ToolRunner.App.A11y.Heard = (t, i) => said.Add((t, i));
		try {
			ToolRunner.App.A11y.Say(null!, "Your turn in the game", true);
			ToolRunner.App.A11y.Say(null!, "   ");
			ToolRunner.App.A11y.Say(null!, new string('x', 900));
		} finally {
			ToolRunner.App.A11y.Heard = null;
		}
		Equal(2, said.Count, "blank text is never said");
		Check(said[0].Item2 && said[0].Item1 == "Your turn in the game", "an important line as important");
		Check(said[1].Item1.Length < 620, "a long text is cut for the reader");
	}

	// a clean removal sends only the program's own data and its own run backups to the Recycle Bin; setups stay
	// when asked; nothing goes while a run did not finish
	static void T_CleanRemovalTakesOnlyItsOwn()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()));
		var setupsDir = Path.GetDirectoryName(f.SetupFile)!;
		var own = Path.Combine(setup.BackupDir, "2026-11-01 20-00-00 " + setup.Name);
		var other = Path.Combine(setup.BackupDir, "2026-11-01 20-00-00 Someone Else");
		var stray = Path.Combine(setup.BackupDir, "my notes");
		foreach (var d in new[] { own, other, stray }) {
			Directory.CreateDirectory(d);
		}
		Check(Files.SamePath(setupsDir, Path.Combine(f.Data, "Setups")), "the fake setup lives in the data folder's Setups");
		// its ProgramData work folder (FasterNGIO's wrapper) - a stand-in, the real one is never touched by a test
		var keepRoots = DataRemoval.WorkRoots;
		var work = Path.Combine(f.Root, "ProgramData ToolRunner");
		Directory.CreateDirectory(work);
		DataRemoval.WorkRoots = [work, Path.Combine(f.Root, "not there")];
		try {
			CleanRemovalPlans(f, setup, setupsDir, own, other, stray, work);
		} finally {
			DataRemoval.WorkRoots = keepRoots;
		}
	}

	static void CleanRemovalPlans(FakeMo2 a_f, Setup a_setup, string a_setupsDir, string a_own, string a_other, string a_stray, string a_work)
	{
		Check(DataRemoval.Make(a_f.Data, true).Remove.Contains(a_work, StringComparer.OrdinalIgnoreCase), "its ProgramData work folder goes too");
		Check(!DataRemoval.Make(a_f.Data, true).Remove.Any(p => p.EndsWith("not there", StringComparison.Ordinal)), "a work folder that does not exist is not listed");
		// a setup deleted since: its run record still names its backup, which goes too - a record pointing at a folder not named
		// like a run backup removes nothing
		var goneBackup = Path.Combine(a_setup.BackupDir, "2026-10-30 19-00-00 Gone Setup");
		Directory.CreateDirectory(Path.Combine(goneBackup, "profile Test"));
		// named like a run backup but holding no copy of a profile: not one, never taken
		var lookalike = Path.Combine(a_f.Root, "2026-10-30 20-00-00 Lookalike");
		Directory.CreateDirectory(lookalike);
		foreach (var (id, name, backup) in new[] { ("2026-10-30 19-00-00 Gone Setup", "Gone Setup", goneBackup), ("2026-10-30 19-30-00 Odd", "Odd", a_stray),
			         ("2026-10-30 20-00-00 Lookalike", "Lookalike", lookalike) }) {
			var done = new Journal { Id = id, Folder = Path.Combine(Runner.RunsDir(a_f.Data), id), SetupFile = Path.Combine(a_setupsDir, name + ".json"), SetupName = name,
				Profile = "Test", BackupDir = backup, Started = DateTime.Now, State = RunState.Finished, Pid = 0 };
			Directory.CreateDirectory(done.Folder);
			done.Save();
		}
		var all = DataRemoval.Make(a_f.Data, false);
		Equal<string?>(null, all.Refused, "nothing refused");
		Check(all.Remove.Contains(goneBackup, StringComparer.OrdinalIgnoreCase), "a deleted setup's run backup goes, found through its run record");
		Check(!all.Remove.Contains(lookalike, StringComparer.OrdinalIgnoreCase), "a folder a record names that holds no profile copy stays");
		Equal(all.Remove.Count, all.Remove.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "each folder listed once");
		Check(all.Remove.Contains(a_own, StringComparer.OrdinalIgnoreCase), "this setup's run backup goes");
		Check(!all.Remove.Contains(a_other, StringComparer.OrdinalIgnoreCase) && !all.Remove.Contains(a_stray, StringComparer.OrdinalIgnoreCase), "another setup's backup and a stray folder stay");
		Check(all.Remove.Any(p => Files.SamePath(p, a_f.Data)), "the data folder goes whole");
		var keep = DataRemoval.Make(a_f.Data, true);
		Check(keep.Kept.Any(p => Files.SamePath(p, a_setupsDir)) && !keep.Remove.Any(p => Files.SamePath(p, a_setupsDir) || Files.SamePath(p, a_f.Data)), "the setups kept, so the data folder itself stays");
		// what would not fit the Recycle Bin is never handed to it (Windows would delete it for good): left in place and named
		var room = DataRemoval.BinRoom;
		try {
			DataRemoval.BinRoom = _ => 1;
			var sent = new List<string>();
			var left = DataRemoval.Carry(keep, p => { sent.Add(p); return null; });
			Check(left.Any(l => l.Contains("more than its drive's Recycle Bin takes", StringComparison.Ordinal)), "an entry bigger than the bin is left in place and named: " + string.Join(" | ", left));
			Check(sent.All(p => (Directory.Exists(p) ? Files.SizeOf(p) : new FileInfo(p).Length) <= 1), "and only what fits is sent");
			DataRemoval.BinRoom = _ => 0;
			Check(DataRemoval.Carry(keep, _ => null).Any(l => l.Contains("keeps no Recycle Bin", StringComparison.Ordinal)), "on a drive with no bin it is left in place too");
			var handed = new List<string>();
			DataRemoval.Carry(keep, p => { handed.Add(p); return null; });
			Equal(0, handed.Count, "nothing at all is handed to a bin that is not there, an empty file or folder included");
		} finally {
			DataRemoval.BinRoom = room;
		}
		var binned = new List<string>();
		Equal(0, DataRemoval.Carry(keep, p => { binned.Add(p); return null; }).Count, "all of it went");
		Check(binned.Count == keep.Remove.Count(p => File.Exists(p) || Directory.Exists(p)), "each entry sent once");
		var j = new Journal { Id = "x", Folder = Path.Combine(Runner.RunsDir(a_f.Data), "2026-11-02 21-00-00 " + a_setup.Name), SetupFile = a_f.SetupFile, SetupName = a_setup.Name,
			Started = DateTime.Now, State = RunState.Running, Pid = 0 };
		Directory.CreateDirectory(j.Folder);
		j.Save();
		var refused = DataRemoval.Make(a_f.Data, false);
		Check(refused.Refused != null, "a run that did not finish refuses the removal");
		var threw = false;
		try {
			DataRemoval.Carry(refused, _ => throw new InvalidOperationException("never reached"));
		} catch (InvalidOperationException e) when (e.Message == refused.Refused) {
			threw = true;
		}
		Check(threw, "carrying a refused plan says why and sends nothing");
	}

	// the bug-report zip holds the run's logs, its setup and the versions, with the player's names masked
	// everywhere (paths included) - and a name inside an ordinary word is left alone
	static void T_BugReportMasksThePlayersNames()
	{
		var names = new List<(string, string)> { ("Sam-PC", "<this PC>"), ("Sam", "<you>") };
		Equal(@"C:\Users\<you>\Documents on <this PC>; Samland stays", BugReport.Mask(@"C:\Users\sam\Documents on SAM-PC; Samland stays", names), "whole names, any case");
		var realFolder = BugReport.UserFolder;
		try {
			// a short user name is never in the names list, but its user folder is masked in every way a path is written
			BugReport.UserFolder = @"C:\Users\Li";
			Equal(@"<your user folder>\x, <your user folder>\\y, <your user folder>/z, C:\Users\Lisa stays",
				BugReport.Mask(@"C:\Users\Li\x, C:\\Users\\Li\\y, C:/Users/Li/z, C:\Users\Lisa stays", []), "the user folder, however short its name");
		} finally {
			BugReport.UserFolder = realFolder;
		}
		Equal(@"""C:\\Users\\<you>\\x""", BugReport.Mask(@"""C:\\Users\\Jos\u00E9\\x""", [("José", "<you>")]), "a name as a JSON file escapes it");
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()));
		var me = Environment.UserName;
		var folder = Path.Combine(Runner.RunsDir(f.Data), "2026-11-02 21-00-00 " + setup.Name);
		Directory.CreateDirectory(folder);
		var j = new Journal { Id = "r", Folder = folder, SetupFile = f.SetupFile, SetupName = setup.Name, Started = DateTime.Now, State = RunState.Failed };
		j.Save();
		File.WriteAllText(Path.Combine(folder, "log.txt"), $"21:00:00  started from C:\\Users\\{me}\\Desktop on {Environment.MachineName}\n");
		File.WriteAllText(Path.Combine(folder, RunLog.SimpleName), "21:00  1/1 Pandora started\n");
		var zip = BugReport.Make(f.Data, Journal.Load(folder), Path.Combine(f.Root, "reports"), "1.0");
		Check(File.Exists(zip) && !File.Exists(zip + ".tmp"), "the zip is there, no half file beside it");
		using var z = System.IO.Compression.ZipFile.OpenRead(zip);
		var got = z.Entries.Select(e => e.FullName).ToList();
		foreach (var want in new[] { "about.txt", "run/log.txt", "run/" + RunLog.SimpleName, "run/journal.json", "setup.json" }) {
			Check(got.Contains(want), $"{want} in the zip: {string.Join(", ", got)}");
		}
		foreach (var e in z.Entries) {
			using var r = new StreamReader(e.Open());
			var text = r.ReadToEnd();
			if (me.Length >= 3) {
				Check(!System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(me) + @"(?![\p{L}\p{N}])",
					System.Text.RegularExpressions.RegexOptions.IgnoreCase), $"{e.FullName} never names the Windows user");
			}
			if (e.FullName == "run/log.txt") {
				// the user folder itself is masked first (a short user name too), so the path reads as the folder's mask
				var userFolderMasked = text.Contains(@"<your user folder>\Desktop", StringComparison.Ordinal) || text.Contains(@"C:\Users\<you>\Desktop", StringComparison.Ordinal);
				Check(userFolderMasked && text.Contains("<this PC>", StringComparison.Ordinal), "the log's path and PC name masked: " + text);
			}
			if (e.FullName == "about.txt") {
				Check(text.Contains(Brand.Name + " 1.0", StringComparison.Ordinal) && text.Contains("Windows:", StringComparison.Ordinal), "the versions: " + text);
			}
		}
	}

	// a setup renamed since its last runs: the backups those runs made carry its older name, and its run records still name them.
	// They are counted and cleaned up with the rest; a folder no record of the setup names, or one that stands elsewhere, never is
	static void T_BackupsMadeUnderAnOlderNameAreStillTheSetups()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()));
		var runs = Runner.RunsDir(f.Data);
		string Named(string a_when, string a_name) => Path.Combine(setup.BackupDir, a_when + " " + a_name);
		void Backup(string a_dir, string a_id, string a_name, bool a_record = true)
		{
			Directory.CreateDirectory(Path.Combine(a_dir, "profile " + setup.Profile));
			File.WriteAllBytes(Path.Combine(a_dir, "x.bin"), new byte[1000]);
			if (a_record) {
				var j = new Journal { Id = a_id, Folder = Path.Combine(runs, a_id), SetupFile = f.SetupFile, SetupName = a_name, Profile = setup.Profile, Started = DateTime.Now,
					State = RunState.Finished, BackupDir = a_dir };
				Directory.CreateDirectory(j.Folder);
				j.Save();
			}
		}
		Backup(Named("2026-11-01 20-00-00", "Old Name"), "r1", "Old Name");
		Backup(Named("2026-11-02 20-00-00", "Old Name"), "r2", "Old Name");
		Backup(Named("2026-11-03 20-00-00", setup.Name), "r3", setup.Name);
		Backup(Named("2026-11-04 20-00-00", setup.Name), "r4", setup.Name);
		var stranger = Named("2026-10-01 20-00-00", "Old Name");
		Backup(stranger, "none", "Old Name", false);
		var elsewhere = Path.Combine(f.Root, "Elsewhere", "2026-10-02 20-00-00 Old Name");
		Backup(elsewhere, "r0", "Old Name");
		Equal(2, Runner.BackupsOf(setup).Count, "by their names alone: the ones under its name of today");
		var listed = Runner.BackupsOf(setup, runs);
		Equal(4, listed.Count, "with its run records: the ones made under its older name too - " + string.Join(", ", listed.Select(b => b.Dir.Name)));
		Check(listed[0].Dir.Name.StartsWith("2026-11-04", StringComparison.Ordinal) && listed[3].Dir.Name.StartsWith("2026-11-01", StringComparison.Ordinal), "newest first, whatever the name");
		var removed = Runner.PruneBackupsOf(setup, runs, null, 3, RunLog.Null);
		Equal(1, removed.Count, "the oldest of the four goes: " + string.Join(", ", removed));
		Check(!Directory.Exists(Named("2026-11-01 20-00-00", "Old Name")) && Directory.Exists(Named("2026-11-02 20-00-00", "Old Name")), "the oldest under the old name went, the newer one stays");
		Check(Directory.Exists(stranger) && Directory.Exists(elsewhere), "a folder no record of the setup names, and one that stands elsewhere, are never touched");
	}

	// the program's own versions are compared number by number - never by the tools' rule, which reads a short version as the
	// end of a long one
	static void T_TheProgramsOwnVersionsCompareNumberByNumber()
	{
		Check(ProgramUpdate.IsNewer("1.1.2", "1.2"), "1.2 comes after 1.1.2");
		Check(ProgramUpdate.IsNewer("1.2.1", "v2.1"), "2.1 comes after 1.2.1");
		Check(ProgramUpdate.IsNewer("1.0.0.0", "v1.0.1"), "a third number");
		Check(ProgramUpdate.IsNewer("1.9", "1.10"), "ten comes after nine");
		Check(!ProgramUpdate.IsNewer("1.1.0.0", "v1.1"), "the same version written two ways");
		Check(!ProgramUpdate.IsNewer("1.2", "1.1.9"), "an older one");
		Check(!ProgramUpdate.IsNewer("1.2", "unknown") && !ProgramUpdate.IsNewer("", "1.2"), "a value with no number says nothing");
	}

	// a tool's version is remembered for the very program a step runs: a setup whose step runs another copy of the tool never
	// reads the first one's version as its own tool having changed, and a store that does not read holds nothing
	static void T_AToolsVersionIsKeptForTheProgramItIs()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var one = Path.Combine(f.Root, "Tool one", "tool.exe");
		var two = Path.Combine(f.Root, "Tool two", "tool.exe");
		foreach (var (exe, at) in new[] { (one, new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)), (two, new DateTime(2026, 2, 2, 10, 0, 0, DateTimeKind.Utc)) }) {
			Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
			File.WriteAllText(exe, "x");
			File.SetLastWriteTimeUtc(exe, at);
		}
		var setup = f.WriteSetup(("pandora", new JsonObject { ["exe"] = one }));
		var first = new StepSettings(new JsonObject { ["exe"] = one });
		var second = new StepSettings(new JsonObject { ["exe"] = two });
		Equal(0, ToolVersions.Check(setup, mo2, "pandora", first).Count(), "nothing is known yet");
		ToolVersions.Remember(setup, mo2, ["pandora"]);
		Equal(0, ToolVersions.Check(setup, mo2, "pandora", first).Count(), "the same program at the same version");
		Equal(0, ToolVersions.Check(setup, mo2, "pandora", second).Count(), "another copy of the tool is another program: nothing is known of it");
		File.SetLastWriteTimeUtc(one, new DateTime(2026, 3, 3, 10, 0, 0, DateTimeKind.Utc));
		Equal(1, ToolVersions.Check(setup, mo2, "pandora", first).Count(), "the program itself changed: said");
		File.WriteAllText(Path.Combine(Path.GetDirectoryName(f.SetupFile)!, "tool versions.json"), "{ broken");
		Equal(0, ToolVersions.Check(setup, mo2, "pandora", first).Count(), "a store that does not read holds nothing");
	}

	// a setup's run backups are listed newest first with their size, and the clean-up keeps the newest few -
	// never another setup's folder or one an unfinished run still needs
	static void T_BackupCleanUpKeepsTheNewest()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()));
		string Run(string a_when) => Path.Combine(setup.BackupDir, a_when + " " + setup.Name);
		foreach (var w in new[] { "2026-11-01 20-00-00", "2026-11-02 20-00-00", "2026-11-03 20-00-00", "2026-11-04 20-00-00" }) {
			Directory.CreateDirectory(Run(w));
			File.WriteAllBytes(Path.Combine(Run(w), "x.bin"), new byte[1000]);
		}
		var stranger = Path.Combine(setup.BackupDir, "2026-11-01 20-00-00 Other Setup");
		Directory.CreateDirectory(stranger);
		var listed = Runner.BackupsOf(setup);
		Equal(4, listed.Count, "only this setup's own run folders");
		Check(listed[0].Dir.Name.StartsWith("2026-11-04", StringComparison.Ordinal) && listed.All(b => b.Bytes == 1000), "newest first, each with its size");
		// the oldest is still needed by a run that did not finish
		var j = new Journal { Id = "u", Folder = Path.Combine(Runner.RunsDir(f.Data), "u"), SetupFile = f.SetupFile, SetupName = setup.Name, Started = DateTime.Now,
			State = RunState.Running, BackupDir = Run("2026-11-01 20-00-00") };
		Directory.CreateDirectory(j.Folder);
		j.Save();
		var removed = Runner.PruneBackupsOf(setup, Runner.RunsDir(f.Data), null, 2, RunLog.Null);
		Equal(1, removed.Count, "one removed: " + string.Join(", ", removed));
		Check(!Directory.Exists(Run("2026-11-02 20-00-00")), "the old one nobody needs went");
		Check(Directory.Exists(Run("2026-11-01 20-00-00")) && Directory.Exists(Run("2026-11-03 20-00-00")) && Directory.Exists(Run("2026-11-04 20-00-00")),
			"the newest two and the one an unfinished run needs stay");
		Check(Directory.Exists(stranger), "another setup's folder is never touched");
		Equal(0, Runner.PruneBackupsOf(setup, Runner.RunsDir(f.Data), null, 0, RunLog.Null).Count, "0 = keep them all");
	}

	// a newer version of the program is told at most once a day (one look on the web, then the remembered answer),
	// a version set aside with "Not now" is not told again but the next newer one is, the switch turns the look off, and a look that
	// fails stays quiet
	static void T_ANewVersionIsToldOnceADay()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner test new version " + Guid.NewGuid().ToString("N"));
		var looks = 0;
		var newest = "1.1";
		var fail = false;
		ProgramUpdate.Probe = _ => {
			++looks;
			return fail ? throw new System.Net.Http.HttpRequestException("offline") : Task.FromResult("v" + newest);
		};
		try {
			var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
			string? Ask(string a_current, DateTime a_at) => ProgramUpdate.Newer(dir, a_current, a_at, CancellationToken.None).GetAwaiter().GetResult();
			Equal("v1.1", Ask("1.0", now), "a newer version is told");
			Equal(1, looks, "one look on the web");
			Equal("v1.1", Ask("1.0", now.AddHours(5)), "told again from what was remembered");
			Equal(1, looks, "no second look within a day");
			Equal(null, Ask("1.1", now.AddHours(6)), "the version you have is nothing new");
			ProgramUpdate.SetAside(dir, "v1.1");
			Equal(null, Ask("1.0", now.AddHours(7)), "a version set aside is not told again");
			newest = "1.2";
			Equal("v1.2", Ask("1.0", now.AddDays(1).AddHours(1)), "the next newer one is told after a day");
			Equal(2, looks, "a day later it looks again");
			ProgramUpdate.SetOff(dir, true);
			Equal(null, Ask("1.0", now.AddDays(3)), "the switch turns it off");
			Equal(2, looks, "switched off, it never looks");
			ProgramUpdate.SetOff(dir, false);
			fail = true;
			Equal(null, Ask("1.0", now.AddDays(4)), "a failed look says nothing");
			var afterFail = looks;
			Equal(null, Ask("1.0", now.AddDays(4).AddHours(3)), "still nothing the same day");
			Equal(afterFail, looks, "a failed look counts as the day's look - never asked again each start");
			Check(ProgramUpdate.Page.StartsWith("https://", StringComparison.Ordinal), "its download page is a web address");
			// switched off while a look is out on the web: the look's answer neither undoes the switch nor is told
			fail = false;
			newest = "1.3";
			ProgramUpdate.Probe = _ => {
				++looks;
				ProgramUpdate.SetOff(dir, true);
				return Task.FromResult("v" + newest);
			};
			Equal(null, Ask("1.0", now.AddDays(6)), "switched off during the look: nothing told");
			Check(ProgramUpdate.IsOff(dir), "and the switch stays off");
		} finally {
			ProgramUpdate.Probe = null;
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// A new version is taken from the program's own GitHub release and put in place of the one
	// that runs. The download is checked before anything is replaced (GitHub's check sum of it, plain file names only, the
	// program inside it at the version asked for), each new file is read back, the old ones are set aside and removed at the
	// next start, a step that fails takes every step before it back, and the data folder beside the program is no part of it.
	static void T_ANewVersionIsDownloadedCheckedAndPutInPlace()
	{
		var top = Path.Combine(Path.GetTempPath(), "Tool Runner test install " + Guid.NewGuid().ToString("N"));
		var program = Path.Combine(top, "program");
		var data = Path.Combine(program, "Tool Runner Data");
		var web = Path.Combine(top, "web");
		Directory.CreateDirectory(Path.Combine(data, "Setups"));
		Directory.CreateDirectory(web);
		string MakeZip(string a_name, params (string Name, string Text)[] a_files)
		{
			var zip = Path.Combine(web, a_name);
			File.Delete(zip);
			using var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
			foreach (var (name, text) in a_files) {
				using var w = new StreamWriter(z.CreateEntry(name).Open());
				w.Write(text);
			}
			return zip;
		}
		static string Sha(string a_file)
		{
			using var f = File.OpenRead(a_file);
			return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)).ToLowerInvariant();
		}
		var exeName = ProgramUpdate.ProgramFile;
		var good = MakeZip("good.zip", (exeName, "the program, version 1.1"), ("README.txt", "readme 1.1"), ("CHANGELOG.txt", "changes 1.1"));
		static JsonObject Answer(string a_zip, string? a_sha = null, string? a_url = null, string? a_name = null) => new() {
			["tag_name"] = "v1.1",
			["assets"] = new JsonArray(
				new JsonObject { ["name"] = "Source code.zip", ["browser_download_url"] = $"https://github.com/{ProgramUpdate.GitHubRepo}/archive/v1.1.zip", ["size"] = 5 },
				// GitHub lists an uploaded "Tool Runner 1.1.zip" under the name it gives it, its space made a dot
				new JsonObject { ["name"] = a_name ?? $"{Brand.Name.Replace(' ', '.')}.1.1.zip", ["browser_download_url"] = a_url ?? $"https://github.com/{ProgramUpdate.GitHubRepo}/releases/download/v1.1/Tool.Runner.1.1.zip",
					["size"] = new FileInfo(a_zip).Length, ["digest"] = "sha256:" + (a_sha ?? Sha(a_zip)) }),
		};
		var serve = good;
		ProgramUpdate.AssetProbe = (_, _) => Task.FromResult<Stream>(File.OpenRead(serve));
		ProgramUpdate.VersionOf = f => File.ReadAllText(f).Contains("version 1.1", StringComparison.Ordinal) ? "1.1.0.0" : "1.0.0.0";
		try {
			static ProgramUpdate.Release Latest(JsonObject a_answer)
			{
				ProgramUpdate.ReleaseProbe = _ => Task.FromResult<JsonObject?>(a_answer);
				return ProgramUpdate.Latest(CancellationToken.None).GetAwaiter().GetResult();
			}
			string Download(ProgramUpdate.Release a_r) => ProgramUpdate.Download(a_r, data, null, CancellationToken.None).GetAwaiter().GetResult();
			bool NothingLeft() => !Directory.Exists(ProgramUpdate.UpdatesDir(data)) || !Directory.EnumerateFileSystemEntries(ProgramUpdate.UpdatesDir(data)).Any();

			// what is not taken at all
			Throws<InvalidOperationException>(() => Latest(Answer(good, a_url: "https://example.com/Tool.Runner.1.1.zip")), "a download from anywhere but the program's own repository");
			Throws<InvalidOperationException>(() => Latest(Answer(good, a_name: "Something else.zip")), "a release without the program's own zip");
			Throws<InvalidOperationException>(() => Latest([]), "an answer with no release");
			var noSum = Answer(good);
			((JsonObject)((JsonArray)noSum["assets"]!)[1]!).Remove("digest");
			Throws<InvalidOperationException>(() => Latest(noSum), "a download GitHub gives no check sum for: never installed from here");
			// what is fetched and then refused: nothing of it stays
			Throws<InvalidOperationException>(() => Download(Latest(Answer(good, a_sha: new string('0', 64)))), "a download whose check sum is not GitHub's");
			Check(NothingLeft(), "and nothing of it is kept");
			serve = MakeZip("folder.zip", (exeName, "the program, version 1.1"), ("sub/evil.txt", "x"));
			Throws<InvalidOperationException>(() => Download(Latest(Answer(serve))), "a zip with a file in a folder");
			serve = MakeZip("up.zip", (exeName, "the program, version 1.1"), ("..\\evil.txt", "x"));
			Throws<InvalidOperationException>(() => Download(Latest(Answer(serve))), "a zip with a file that points out of its folder");
			serve = MakeZip("noexe.zip", ("README.txt", "readme"));
			Throws<InvalidOperationException>(() => Download(Latest(Answer(serve))), "a zip without the program");
			serve = MakeZip("other.zip", (exeName, "the program, version 1.0"));
			Throws<InvalidOperationException>(() => Download(Latest(Answer(serve))), "a program that says it is another version");
			serve = Path.Combine(web, "nozip.zip");
			File.WriteAllText(serve, "this is no zip");
			Throws<InvalidOperationException>(() => Download(Latest(Answer(serve))), "a download that is no zip");
			// one file twice (as Windows sees names): the second copy would land on the first and on its set-aside copy
			var twice = MakeZip("twice.zip", (exeName, "the program, version 1.1"), (exeName.ToUpperInvariant(), "the program, version 1.1"));
			serve = twice;
			Throws<InvalidOperationException>(() => Download(Latest(Answer(serve))), "a zip holding one file twice");
			// a download that stops coming is ended, never waited on for good
			var quietWas = ToolUpdates.FetchQuiet;
			ToolUpdates.FetchQuiet = TimeSpan.FromMilliseconds(300);
			ProgramUpdate.AssetProbe = (_, _) => Task.FromResult<Stream>(new SilentStream(7));
			try {
				var clock = System.Diagnostics.Stopwatch.StartNew();
				Throws<System.Net.Http.HttpRequestException>(() => Download(Latest(Answer(good))), "a download that sends a little and then nothing");
				Check(clock.Elapsed < TimeSpan.FromSeconds(10), $"ended after the quiet stretch, not later ({clock.Elapsed.TotalSeconds:0.0} s)");
			} finally {
				ToolUpdates.FetchQuiet = quietWas;
				ProgramUpdate.AssetProbe = (_, _) => Task.FromResult<Stream>(File.OpenRead(serve));
			}
			Check(NothingLeft(), "none of the refused downloads is kept");
			// the name as it was uploaded is still the program's own download
			Equal($"{Brand.Name} 1.1.zip", Latest(Answer(good, a_name: $"{Brand.Name} 1.1.zip")).Asset, "a download listed under its name with spaces");

			// the real thing
			serve = good;
			var release = Latest(Answer(good));
			Equal("v1.1", release.Version, "the release's version");
			Equal($"{Brand.Name.Replace(' ', '.')}.1.1.zip", release.Asset, "the download found under the name GitHub lists, and kept under it");
			var zip = Download(release);
			Check(File.Exists(zip) && Path.GetDirectoryName(zip) == ProgramUpdate.UpdatesDir(data) && !File.Exists(zip + ".part"), "the checked download is in the data folder's Updates, whole");
			File.WriteAllText(Path.Combine(program, exeName), "the program, version 1.0");
			File.WriteAllText(Path.Combine(program, "README.txt"), "readme 1.0");
			File.WriteAllText(Path.Combine(data, "Setups", "mine.json"), "{ \"name\": \"mine\" }");

			// a file of the program that cannot be replaced: every step is taken back
			using (File.Open(Path.Combine(program, "README.txt"), FileMode.Open, FileAccess.Read, FileShare.None)) {
				Throws<IOException>(() => ProgramUpdate.Install(zip, program, data), "a file held by another program stops the install");
			}
			Equal("the program, version 1.0", File.ReadAllText(Path.Combine(program, exeName)), "after a failed install the program is the old one, under its own name");
			Equal("readme 1.0", File.ReadAllText(Path.Combine(program, "README.txt")), "and so is every other file");
			Equal("README.txt," + exeName, string.Join(",", Directory.GetFiles(program).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)), "nothing new and nothing set aside is left beside it");

			// never while a run or a put-back goes
			using (var held = new ManualResetEventSlim(false))
			using (var done = new ManualResetEventSlim(false)) {
				var other = new Thread(() => {
					using var hold = Runner.HoldIfFree();
					held.Set();
					done.Wait();
				});
				other.Start();
				held.Wait();
				try {
					Throws<InvalidOperationException>(() => ProgramUpdate.Install(zip, program, data), "an install beside a run");
				} finally {
					done.Set();
					other.Join();
				}
			}
			Equal("the program, version 1.0", File.ReadAllText(Path.Combine(program, exeName)), "and nothing was touched");

			// a zip holding one file twice is refused before anything is written
			Throws<InvalidOperationException>(() => ProgramUpdate.Install(twice, program, data), "an install of a zip holding one file twice");
			Equal("README.txt," + exeName, string.Join(",", Directory.GetFiles(program).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)), "and nothing was written or set aside");

			var started = ProgramUpdate.Install(zip, program, data);
			Equal(Path.Combine(program, exeName), started, "the program to start again");
			Equal("the program, version 1.1", File.ReadAllText(started), "the new program is in place");
			Equal("readme 1.1", File.ReadAllText(Path.Combine(program, "README.txt")), "with its other files");
			Equal("changes 1.1", File.ReadAllText(Path.Combine(program, "CHANGELOG.txt")), "a file the old version did not have too");
			var oldExe = Path.Combine(program, Path.GetFileNameWithoutExtension(exeName) + " (old).exe");
			Equal("the program, version 1.0", File.ReadAllText(oldExe), "the old program is set aside, not written over");
			Check(File.Exists(Path.Combine(program, "README (old).txt")) && Directory.GetFiles(program, "*.new").Length == 0, "and so is each file it replaced; nothing half written is left");
			Equal("{ \"name\": \"mine\" }", File.ReadAllText(Path.Combine(data, "Setups", "mine.json")), "the data folder beside the program was not touched");

			// never beside a run, a put-back or another install (the lock Install takes): nothing removed, the record kept
			using (var held = new ManualResetEventSlim(false))
			using (var done = new ManualResetEventSlim(false)) {
				var other = new Thread(() => {
					using var hold = Runner.HoldIfFree();
					held.Set();
					done.Wait();
				});
				other.Start();
				held.Wait();
				try {
					ProgramUpdate.CleanUp(data);
				} finally {
					done.Set();
					other.Join();
				}
			}
			Check(File.Exists(oldExe) && File.Exists(Path.Combine(program, "README (old).txt")) && File.Exists(zip), "a clean-up while the lock is held elsewhere removes nothing and keeps its record");

			// the next start clears what the install set aside - one that will not go yet stays on record for the start after
			using (File.Open(oldExe, FileMode.Open, FileAccess.Read, FileShare.None)) {
				ProgramUpdate.CleanUp(data);
			}
			Check(File.Exists(oldExe) && !File.Exists(Path.Combine(program, "README (old).txt")) && !File.Exists(zip), "the first clean-up takes what it can (the old program is still ending)");
			ProgramUpdate.CleanUp(data);
			Check(!File.Exists(oldExe) && NothingLeft(), "the next takes the rest, and its own record");
			// a record that names a file in a folder that holds no copy of this program removes nothing there
			var elsewhere = Path.Combine(top, "elsewhere");
			Directory.CreateDirectory(elsewhere);
			var notMine = Path.Combine(elsewhere, "Notes (old).txt");
			File.WriteAllText(notMine, "a file of someone else's");
			Directory.CreateDirectory(ProgramUpdate.UpdatesDir(data));
			File.WriteAllText(Path.Combine(ProgramUpdate.UpdatesDir(data), "installed.json"), System.Text.Json.JsonSerializer.Serialize(new ProgramUpdate.Installed([notMine], Path.Combine(top, "nowhere.zip"))));
			ProgramUpdate.CleanUp(data);
			Check(File.Exists(notMine), "a file outside the program's own folder is never removed by a clean-up");
			Equal("the program, version 1.1", File.ReadAllText(started), "the program in place stays");

			// what is new, said once: this version's own lines, and the setup keys read differently since the version before
			var log = "Tool Runner - changelog\n\n1.1\n- A new thing.\n  It goes on here.\n\n1.0 (not released yet)\n- First version.\n";
			Equal("- A new thing.|  It goes on here.", string.Join("|", ProgramUpdate.WhatsNew(log, "1.0", "1.1")), "this version's lines of the changelog, and nothing a player on 1.0 already had");
			var fromBefore = ProgramUpdate.WhatsNew(log, "0.9", "1.1");
			Check(fromBefore.Any(l => l.Contains("directoryCache", StringComparison.Ordinal)) && fromBefore[0] == "- A new thing.", "coming from before 1.0, the setup key 1.0 reads differently is named too");
			Check(ProgramUpdate.SameVersion("v1.1", "1.1.0.0") && !ProgramUpdate.SameVersion("1.1", "1.2") && !ProgramUpdate.SameVersion("", "1.1"), "a version is the same however it is written");
			Check(!ProgramUpdate.SameVersion("unknown", "v1.1") && !ProgramUpdate.SameVersion("v1.1", "unknown"), "and a value with no version in it is never the same as one");
			Check(!ProgramUpdate.SameVersion("0.1.1.0", "1.1") && !ProgramUpdate.SameVersion("1.1", "0.1.1.0") && !ProgramUpdate.SameVersion("1.1.1", "1.1"), "a short version is never taken as the end of a long one");
			serve = MakeZip("unknown.zip", (exeName, "the program, no version"));
			ProgramUpdate.VersionOf = f => File.ReadAllText(f).Contains("version 1.1", StringComparison.Ordinal) ? "1.1.0.0" : "unknown";
			Throws<InvalidOperationException>(() => Download(Latest(Answer(serve))), "a program that says no version at all");
			// the changelog read off its file: one that does not read gives nothing (the version is then not marked as seen)
			var changelog = Path.Combine(top, "CHANGELOG.txt");
			Check(ProgramUpdate.ReadWhatsNew(changelog, "1.0", "1.1") == null, "a changelog that is not there");
			File.WriteAllText(changelog, log);
			Equal("- A new thing.|  It goes on here.", string.Join("|", ProgramUpdate.ReadWhatsNew(changelog, "1.0", "1.1") ?? []), "a changelog read off its file");
		} finally {
			ProgramUpdate.ReleaseProbe = null;
			ProgramUpdate.AssetProbe = null;
			ProgramUpdate.VersionOf = ToolVersions.Version;
			try {
				Directory.Delete(top, true);
			} catch (IOException) {
			}
		}
	}

	// a download that gives a_first bytes and then nothing more, until it is cancelled
	sealed class SilentStream(int a_first) : Stream
	{
		int _left = a_first;

		public override async ValueTask<int> ReadAsync(Memory<byte> a_buffer, CancellationToken a_ct = default)
		{
			if (_left > 0) {
				var n = Math.Min(_left, a_buffer.Length);
				a_buffer.Span[..n].Fill((byte)'x');
				_left -= n;
				return n;
			}
			await Task.Delay(Timeout.Infinite, a_ct).ConfigureAwait(false);
			return 0;
		}

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] a_buffer, int a_offset, int a_count) => throw new NotSupportedException();
		public override long Seek(long a_offset, SeekOrigin a_origin) => throw new NotSupportedException();
		public override void SetLength(long a_value) => throw new NotSupportedException();
		public override void Write(byte[] a_buffer, int a_offset, int a_count) => throw new NotSupportedException();
	}

	// The client's own timeout covers only an answer's headers. A server on this PC that sends its
	// headers and then nothing is ended after the quiet stretch, and one that keeps sending a byte now and then by the deadline
	static void T_ADownloadThatStopsComingIsEnded()
	{
		var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
		using var stop = new CancellationTokenSource();
		// each connection: the request read, headers promising more than is sent, then a byte every a_every ms (or nothing)
		async Task Serve(int a_every)
		{
			using var client = await listener.AcceptTcpClientAsync(stop.Token);
			var s = client.GetStream();
			var buf = new byte[4096];
			// the request's first piece is enough: the answer goes out whatever it asked
			if (await s.ReadAsync(buf, stop.Token) == 0) {
				return;
			}
			await s.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\n\r\nabc"u8.ToArray(), stop.Token);
			try {
				while (!stop.IsCancellationRequested) {
					await Task.Delay(a_every <= 0 ? Timeout.Infinite : a_every, stop.Token);
					await s.WriteAsync("x"u8.ToArray(), stop.Token);
				}
			} catch (Exception e) when (e is OperationCanceledException or IOException) {
			}
		}
		var quietWas = ToolUpdates.FetchQuiet;
		var deadlineWas = ToolUpdates.FetchDeadline;
		try {
			var url = $"http://127.0.0.1:{port}/Tool.Runner.1.1.zip";
			ToolUpdates.FetchQuiet = TimeSpan.FromMilliseconds(400);
			_ = Serve(0);
			var clock = System.Diagnostics.Stopwatch.StartNew();
			using (var to = new MemoryStream()) {
				var e = Thrown<System.Net.Http.HttpRequestException>(() => ToolUpdates.Fetch(url, to, 1 << 20, null, CancellationToken.None).GetAwaiter().GetResult(), "headers, then nothing");
				Check(e.Message.Contains("nothing came", StringComparison.Ordinal) && clock.Elapsed < TimeSpan.FromSeconds(10), $"ended as stopped, after the quiet stretch ({clock.Elapsed.TotalSeconds:0.0} s: {e.Message})");
			}
			ToolUpdates.FetchQuiet = TimeSpan.FromSeconds(5);
			ToolUpdates.FetchDeadline = TimeSpan.FromMilliseconds(1500);
			_ = Serve(100);
			clock.Restart();
			using (var to = new MemoryStream()) {
				var e = Thrown<System.Net.Http.HttpRequestException>(() => ToolUpdates.Fetch(url, to, 1 << 20, null, CancellationToken.None).GetAwaiter().GetResult(), "a byte now and then, past the deadline");
				Check(e.Message.Contains("did not finish", StringComparison.Ordinal) && clock.Elapsed < TimeSpan.FromSeconds(10) && to.Length > 3, $"ended by the deadline, with what had come so far ({clock.Elapsed.TotalSeconds:0.0} s, {to.Length} bytes: {e.Message})");
			}
			// the caller's own cancel stays a cancel
			using (var mine = new CancellationTokenSource(300))
			using (var to = new MemoryStream()) {
				ToolUpdates.FetchDeadline = TimeSpan.FromMinutes(1);
				_ = Serve(0);
				Throws<OperationCanceledException>(() => ToolUpdates.Fetch(url, to, 1 << 20, null, mine.Token).GetAwaiter().GetResult(), "the caller's own cancel");
			}
		} finally {
			ToolUpdates.FetchQuiet = quietWas;
			ToolUpdates.FetchDeadline = deadlineWas;
			stop.Cancel();
			listener.Stop();
		}
	}

	// a shared setup keeps its steps and options and loses everything of this PC - its Mod Organizer, backup
	// folder, every value holding a path, and the commands it runs before and after a run; it names what the other player sets
	static void T_ASharedSetupCarriesNothingOfThisPc()
	{
		var setup = Setup.FromJson(JsonNode.Parse("""
			{ "name": "My Setup", "mo2": { "exe": "C:\\Games\\MO2\\ModOrganizer.exe", "instance": "D:\\MO2\\Instances\\Main", "profile": "My Profile" },
			  "backups": "D:\\Backups", "beforeRun": ["del /q C:\\Temp\\x"], "afterRun": [], "idleMinutes": 3,
			  "steps": [ { "key": "pgpatcher", "on": true, "output": "PGPatcher Output", "tool_writes": "C:\\Users\\Someone\\pg" },
			             { "key": "dyndolod", "on": false, "output": "DynDOLOD Output", "args": "-o:\"\\\\nas\\lod\"", "lodWorlds": ["Tamriel", "E:\\x"] } ] }
			""")!.AsObject());
		var r = SetupShare.Share(setup, "1.0");
		var text = r.Setup.ToJsonString();
		Check(!SetupShare.HoldsPath(text.Replace("\\\\", "\\")), "no path of this PC is left: " + text);
		Equal("", r.Setup["mo2"]?["exe"]?.ToString(), "Mod Organizer is the other player's to set");
		Equal("My Profile", r.Setup["mo2"]?["profile"]?.ToString(), "the profile name is kept as a hint");
		Check(r.Setup["beforeRun"] == null && r.Setup["backups"] == null, "no commands, no backup folder");
		Equal(3, r.Setup["idleMinutes"]?.GetValue<int>(), "its options are kept");
		Equal(2, (r.Setup["steps"] as JsonArray)?.Count, "every step is kept");
		Equal("PGPatcher Output", r.Setup["steps"]?[0]?["output"]?.ToString(), "a step's own options are kept");
		Equal(1, (r.Setup["steps"]?[1]?["lodWorlds"] as JsonArray)?.Count, "a list keeps its other items");
		Check(r.Removed.Any(x => x.Contains("before", StringComparison.Ordinal)) && r.Removed.Any(x => x.Contains("tool_writes", StringComparison.Ordinal)),
			"what was taken out is named: " + string.Join("; ", r.Removed));
		Check((r.Setup["shared"]?["setYourself"] as JsonArray)?.Count == r.Removed.Count, "the copy itself says what to set");
		// More path forms, names masked in values only, the file name masked, never written over its source,
		// and a PGPatcher settings set named as the other player's to make
		foreach (var p in new[] { @"%ProgramFiles(x86)%\Tool", "//nas/private/tool", @"\\nas\lod\x" }) {
			Check(SetupShare.HoldsPath(p), "a path of this PC: " + p);
		}
		Check(!SetupShare.HoldsPath("https://www.nexusmods.com/skyrimspecialedition/mods/32382") && !SetupShare.HoldsPath("Tamriel"), "a web address or a word is no path");
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner test share " + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try {
			var source = Path.Combine(dir, "Steps Of Sam (shared).json");
			File.WriteAllText(source, """
				{ "name": "Steps Of Sam", "mo2": { "profile": "Sam Profile" },
				  "steps": [ { "step": "pgpatcher", "on": true, "settings": { "settings": "PBR", "output_mod": "Sam Output" } } ] }
				""");
			var named = Setup.Load(source);
			List<(string Name, string Mask)> names = [("Sam", "<you>"), ("Steps", "<this PC>")];
			var shared = SetupShare.Share(named, "1.0", names);
			Check(shared.Setup["steps"] is JsonArray { Count: 1 }, "a key that is also a name stays the setup's own key");
			Equal("<you> Profile", shared.Setup["mo2"]?["profile"]?.ToString(), "a name in a value is masked");
			Equal("pgpatcher", shared.Setup["steps"]?[0]?["step"]?.ToString(), "a step's own key is no name");
			Check(shared.Removed.Any(x => x.Contains("PGPatcher settings set \"PBR\"", StringComparison.Ordinal)), "the settings set it names is the other player's to make: " +
				string.Join("; ", shared.Removed));
			var (file, _) = SetupShare.Write(named, dir, "1.0", names);
			Check(!Path.GetFileName(file).Contains("Sam", StringComparison.OrdinalIgnoreCase), "the file name masked too: " + Path.GetFileName(file));
			Check(File.ReadAllText(source).Contains("Sam Output", StringComparison.Ordinal), "the setup itself is untouched");
			File.WriteAllText(Path.Combine(dir, "Mine (shared).json"), """{ "name": "Mine", "mo2": { "exe": "C:\\MO2\\ModOrganizer.exe" } }""");
			var mine = Setup.Load(Path.Combine(dir, "Mine (shared).json"));
			var (again, _) = SetupShare.Write(mine, dir, "1.0", names);
			Check(!Files.SamePath(again, mine.File) && File.ReadAllText(mine.File).Contains("ModOrganizer.exe", StringComparison.Ordinal),
				"a setup named \"... (shared)\" shared into its own folder is never written over: " + Path.GetFileName(again));
		} finally {
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// a save's character is read off the start of its header (the UESP layout), every character with a save is
	// listed most recently played first, and a picked character limits the in-game steps to that character's saves
	static void T_TheCharacterIsReadOffTheSave()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner test saves " + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try {
			void Save(string a_file, string a_name, DateTime a_at)
			{
				using (var fs = File.Create(Path.Combine(dir, a_file)))
				using (var w = new BinaryWriter(fs, Encoding.Latin1)) {
					w.Write(Encoding.ASCII.GetBytes("TESV_SAVEGAME"));
					w.Write(100u);
					w.Write(12u);
					w.Write(7u);
					w.Write((ushort)a_name.Length);
					w.Write(Encoding.Latin1.GetBytes(a_name));
					w.Write(new byte[64]);
				}
				File.SetLastWriteTimeUtc(Path.Combine(dir, a_file), a_at);
			}
			var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
			Save("Save1_Lydia.ess", "Lydia", t0);
			Save("Save2_Björn.ess", "Björn", t0.AddHours(1));
			Save("Save3_Lydia.ess", "Lydia", t0.AddHours(2));
			File.WriteAllText(Path.Combine(dir, "broken.ess"), "not a save");
			Equal("Björn", SaveHeader.PlayerName(Path.Combine(dir, "Save2_Björn.ess")), "the name is read, its letters too");
			Equal(null, SaveHeader.PlayerName(Path.Combine(dir, "broken.ess")), "a file that is no save gives no name");
			Equal("Lydia, Björn", string.Join(", ", SaveHeader.Characters(dir)), "every character once, the most recently played first");
		} finally {
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// the in-game steps with a character picked: that character's newest save even when another character played since, a save
	// of another character never loaded (named or an earlier clean save), and the final save starting from the picked one's
	static void T_TheInGameStepsUseThePickedCharactersSaves()
	{
		using var f = new FakeMo2();
		File.WriteAllText(Path.Combine(f.ProfileDir, "settings.ini"), "[General]\r\nLocalSaves=true\r\n");
		var p = ProfileOf(f);
		var saves = InGame.SavesDir(p);
		Directory.CreateDirectory(saves);
		void Save(string a_file, string a_name, DateTime a_at)
		{
			using (var fs = File.Create(Path.Combine(saves, a_file + ".ess")))
			using (var w = new BinaryWriter(fs, Encoding.Latin1)) {
				w.Write(Encoding.ASCII.GetBytes("TESV_SAVEGAME"));
				w.Write(100u);
				w.Write(12u);
				w.Write(7u);
				w.Write((ushort)a_name.Length);
				w.Write(Encoding.Latin1.GetBytes(a_name));
				w.Write(new byte[64]);
			}
			File.SetLastWriteTimeUtc(Path.Combine(saves, a_file + ".ess"), a_at);
		}
		var t0 = DateTime.UtcNow.AddDays(-2);
		Save("Save2_Bjorn", "Björn", t0.AddHours(1));
		Save("Save3_Lydia", "Lydia", t0.AddHours(2));
		Save("Tool Runner - clean save", "Lydia", t0.AddHours(3));
		Equal("Save3_Lydia", InGame.NewestPlayerSave(p), "no character picked: anyone's newest (never one the runner made)");
		Equal("Save2_Bjorn", InGame.NewestPlayerSave(p, null, "björn"), "the picked character's newest, though another played since");
		Equal("Lydia", InGame.OtherCharacter(p, "Save3_Lydia", "Björn"), "a save of another character is told apart");
		Equal<string?>(null, InGame.OtherCharacter(p, "Save2_Bjorn", "BJÖRN"), "the picked character's own save, in any case");
		Equal<string?>(null, InGame.OtherCharacter(p, "Save3_Lydia", null), "no character picked: no conflict");
		Equal<DateTime?>(null, InGame.OldCleanSave(p, "Björn"), "an earlier clean save of another character is not used");
		Check(InGame.OldCleanSave(p, "Lydia") != null, "one of the picked character's is");
		var player = InGame.NewestPlayerSave(p, null, "Björn");
		Equal("Save2_Bjorn", InGame.PlaySaveFrom(null, null, InGame.OldCleanSave(p, "Björn"), player, InGame.SaveTime(p, player)).Save,
			"the final save starts from the picked character's save, not the newer clean save of another");
		// the steps themselves pass the picked character on - each case below stops before any game would start
		var setup = f.WriteSetup(("cleansave", new JsonObject()), ("playsave", new JsonObject()));
		setup.Root["character"] = "Björn";
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var runs = Runner.RunsDir(f.Data);
		var j = new Journal { Id = "character", Folder = Path.Combine(runs, "0000 character"), SetupFile = setup.File, SetupName = setup.Name, Profile = "Test", Started = DateTime.Now };
		Directory.CreateDirectory(j.Folder);
		using var log = new RunLog(j.Folder);
		var ctx = new RunContext { Setup = setup, Mo2 = mo2, Profile = p, Control = new Mo2Control(mo2, "Test", log), Log = log, Journal = j,
			Cancel = CancellationToken.None, BackupFolder = Path.Combine(f.Backups, "character") };
		Equal("Björn", InGame.Character(ctx), "the setup's character is read");
		string Refused(Action a_step)
		{
			try {
				a_step();
			} catch (StepFailedException e) {
				return e.Message;
			}
			return "(not refused)";
		}
		var named = new StepSettings(new JsonObject { ["save"] = "Save3_Lydia" });
		Check(Refused(() => InGame.Deactivate(ctx, named)).Contains("is Lydia's, not Björn's", StringComparison.Ordinal), "the clean save refuses a named save of another character");
		Check(Refused(() => InGame.PlaySave(ctx, named)).Contains("is Lydia's, not Björn's", StringComparison.Ordinal), "so does the final save");
		File.Delete(Path.Combine(saves, "Save2_Bjorn.ess"));
		Equal("no save to load", Refused(() => InGame.PlaySave(ctx, new StepSettings(new JsonObject()))),
			"with no save of the picked character, the final save never falls back to another character's clean save");
		// the clean save's own pick on the automatic route (DevBench ticked - a stand-in file, nothing runs): no save of the picked
		// character is refused by name before anything starts, never another character's newest taken instead
		var dev = Path.Combine(f.Mods, "DevBench", "SKSE", "Plugins");
		Directory.CreateDirectory(dev);
		File.WriteAllBytes(Path.Combine(dev, "devbench.dll"), []);
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+DevBench\r\n" + f.Text("modlist.txt"));
		Check(DevBench.InProfile(p), "(DevBench ticked: the automatic route)");
		var auto = Refused(() => InGame.Deactivate(ctx, new StepSettings(new JsonObject())));
		Check(auto.Contains("no save of Björn", StringComparison.Ordinal), "the clean save's own pick keeps to the picked character: " + auto);
	}

	// the log box: only whole new lines, a letter never cut in two, a big log only its last part, and a replaced log started over
	// even when the new one has already grown past where the last look stopped
	static void T_TheLogBoxFollowsAReplacedLog()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner test log tail " + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var log = Path.Combine(dir, "log.txt");
		try {
			var tail = new LogTail();
			Equal((null, false), tail.Read(null), "no run, nothing to empty");
			File.WriteAllText(log, "12:00:00  run A begins\n12:00:01  half a li");
			Equal(("12:00:00  run A begins\n", true), tail.Read(log), "a new log: the box emptied, the whole lines shown");
			File.AppendAllText(log, "ne - Björn\n");
			Equal(("12:00:01  half a line - Björn\n", false), tail.Read(log), "the rest of a line once it is whole, its letters too");
			Equal((null, false), tail.Read(log), "nothing new, nothing shown");
			File.WriteAllText(log, "13:30:00  run B begins, a much longer first line than run A had\n13:30:01  next\n");
			Equal(("13:30:00  run B begins, a much longer first line than run A had\n13:30:01  next\n", true), tail.Read(log),
				"a replaced log that already grew past the old place starts over");
			File.WriteAllText(log, "1\n");
			Equal(("1\n", true), tail.Read(log), "a shorter replaced log starts over");
			tail.Restart();
			Equal(("1\n", true), tail.Read(log), "asked again (the other kind of log) - from the start");
			File.WriteAllText(log, string.Concat(Enumerable.Repeat("0123456789abcdef0123456789abcdef0123456789abcdef012345678\n", 5000)));
			var (big, reset) = tail.Read(log);
			Check(reset && big != null && big.Length <= LogTail.Window && big.StartsWith("0123", StringComparison.Ordinal) && big.EndsWith('\n'),
				"a big log: only its last part, from a whole line");
		} finally {
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// the run's command watcher let go of while its look is still going (a Stop's OnStop ending a slow tool): Dispose returns after
	// its wait, the look finishes later and sets the wait's event on the timer's thread - which must still be there (a released
	// event would throw on that thread and end the program), and the look arms no new look
	static void T_TheCommandWatcherLetsGoWhileALookIsHeld()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner test commands " + Guid.NewGuid().ToString("N"));
		var keep = Commands.DisposeWait;
		Commands.DisposeWait = TimeSpan.FromMilliseconds(200);
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		using var finished = new ManualResetEventSlim();
		var looks = 0;
		try {
			Directory.CreateDirectory(dir);
			using var c = new Commands(new Journal { Folder = dir }, () => { }) {
				OnStop = () => {
					Interlocked.Increment(ref looks);
					entered.Set();
					release.Wait(TimeSpan.FromSeconds(10));
					finished.Set();
				},
			};
			Commands.Send(dir, "stop");
			c.Start();
			Check(entered.Wait(TimeSpan.FromSeconds(5)), "the look began (the stop seen)");
			var sw = Stopwatch.StartNew();
			c.Dispose();
			Check(sw.Elapsed < TimeSpan.FromSeconds(3), $"Dispose returned after its wait, not the look's end ({sw.ElapsedMilliseconds} ms)");
			release.Set();
			Check(finished.Wait(TimeSpan.FromSeconds(5)), "the held look finished");
			// the timer sets the event right after the callback returns - give it time, then the program must still be here
			Thread.Sleep(1500);
			Check(c.Stopped, "the stop was taken");
			Equal(1, Volatile.Read(ref looks), "no new look after Dispose");
		} finally {
			Commands.DisposeWait = keep;
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// the settings search names the other steps whose settings hold its word - in a setting's name, its label or its value, a list's
	// items too - never the step already open
	static void T_TheSettingsSearchNamesOtherSteps()
	{
		var steps = new List<JsonObject> {
			new() { ["step"] = "texgen", ["settings"] = new JsonObject { ["output_mod"] = "TexGen Output" } },
			new() { ["step"] = "dyndolod", ["settings"] = new JsonObject { ["lodWorlds"] = new JsonArray("Tamriel", "Solstheim") } },
			new() { ["step"] = "pandora", ["settings"] = new JsonObject() },
		};
		Equal("TexGen", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "texgen out", -1)), "a value");
		Equal("DynDOLOD", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "solstheim", -1)), "a list's item, any case");
		Equal("TexGen", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "output_mod", -1)), "a setting's own name");
		Equal("", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "TexGen Output", 0)), "never the step already open");
		Equal(0, ToolRunner.App.SettingsPanel.StepsWith(steps, " ", -1).Count, "no word, no steps");
		// in another language a setting is found by the label the step editor shows, and still by its English one
		ToolRunner.App.WindowWords.Use(new Dictionary<string, string> { ["Output mod"] = "Мод із результатом" });
		try {
			Equal("TexGen", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "мод із результ", -1)), "a translated label, any case");
			Equal("TexGen", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "Output mod", -1)), "the English label too");
		} finally {
			ToolRunner.App.WindowWords.Use(new Dictionary<string, string>());
		}
		// a setup file may hold a setting with no name at all: it has no label, and nothing fails over it
		steps.Add(new() { ["step"] = "acmos", ["settings"] = new JsonObject { [""] = "nameless" } });
		Equal("ACMOS Road Generator", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "nameless", -1)), "a setting with an empty name is still searched");
		// in another language the label is searched by the words the window shows, and still by its English ones
		try {
			ToolRunner.App.WindowWords.Use(new Dictionary<string, string> { ["Output mod"] = "Ausgabe-Mod", ["{0} min"] = "{0} Minuten" });
			Equal("TexGen", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "ausgabe", -1)), "a label in the window's language");
			Equal("TexGen", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "output mod", -1)), "and in English all the same");
			Equal("5 Minuten", ToolRunner.App.MainViewModel.Long(TimeSpan.FromMinutes(5)), "a time's unit in the window's language");
		} finally {
			ToolRunner.App.WindowWords.Use(new Dictionary<string, string>());
		}
		Equal("5 min", ToolRunner.App.MainViewModel.Long(TimeSpan.FromMinutes(5)), "and in English");
	}

	// a step dragged to any place, a check that also looks for updates, windows that keep their size
	static void T_StepsAreDraggedAndWindowsKeepTheirSize()
	{
		// where a dragged row lands: its place in the list afterwards (taking it out first moves the rows after it up by one)
		Equal(0, ToolRunner.App.MainWindow.DropPlace(3, 0, false), "on the first row's upper half: it becomes the first");
		Equal(1, ToolRunner.App.MainWindow.DropPlace(3, 0, true), "on its lower half: the second");
		Equal(3, ToolRunner.App.MainWindow.DropPlace(1, 3, true), "from above, under the last of four rows: the last");
		Equal(2, ToolRunner.App.MainWindow.DropPlace(1, 3, false), "from above, on a row's upper half: right before that row");
		Equal(1, ToolRunner.App.MainWindow.DropPlace(1, 1, true), "on its own lower half: it stays");
		Equal(1, ToolRunner.App.MainWindow.DropPlace(1, 1, false), "on its own upper half: it stays");
		Equal(1, ToolRunner.App.MainWindow.DropPlace(1, 0, true), "right under the row above it: it stays");
		Equal(1, ToolRunner.App.MainWindow.DropPlace(1, 2, false), "right above the row below it: it stays");

		// what the check says about the tools' versions
		var newer = new ToolUpdates.Result("synthesis", "Synthesis", "0.35", "0.36", true, "https://example.invalid/synthesis", null);
		var same = new ToolUpdates.Result("dyndolod", "TexGen / DynDOLOD", "3.0", "3.0", false, "https://example.invalid/dyndolod", null, 2);
		var unasked = new ToolUpdates.Result("pandora", "Pandora", "?", "", false, "https://example.invalid/pandora", "could not ask its page: offline");
		var lines = ToolRunner.App.MainViewModel.UpdateLines([newer, same]);
		Check(lines.Count == 1 && lines[0].Contains("Synthesis 0.36 is out (you have 0.35)", StringComparison.Ordinal) && lines[0].Contains("by hand", StringComparison.Ordinal),
			"a newer tool is one line with both versions and how it is installed: " + string.Join(" | ", lines));
		Equal("Updates: every tool is up to date.", string.Join(" | ", ToolRunner.App.MainViewModel.UpdateLines([same])), "nothing newer: one line for all");
		Check(string.Join(" | ", ToolRunner.App.MainViewModel.UpdateLines([same, unasked])).Contains("1 tool(s) could not be checked", StringComparison.Ordinal), "a tool that could not be asked is never counted as up to date");
		Equal(0, ToolRunner.App.MainViewModel.UpdateLines([]).Count, "a setup with no tool to ask: nothing said");

		// a remembered size is held to what the screen has room for now
		Equal(900.0, ToolRunner.App.UiState.Fit(900, 780, 420, 1920), "a kept size that fits is used");
		Equal(780.0, ToolRunner.App.UiState.Fit(null, 780, 420, 1920), "none kept: the usual one");
		Equal(1280.0, ToolRunner.App.UiState.Fit(2400, 780, 420, 1280), "kept on a bigger screen: held to this one");
		Equal(420.0, ToolRunner.App.UiState.Fit(100, 780, 420, 1920), "never under the least it needs");
		Equal(300.0, ToolRunner.App.UiState.Fit(100, 780, 420, 300), "unless the screen itself is smaller");
		// a window's least size is held to the screen too: a least height the screen does not have won over the height given
		Equal(300.0, ToolRunner.App.UiState.Least(380, 300), "a least size above the screen's room is the room");
		Equal(380.0, ToolRunner.App.UiState.Least(380, 1040), "and stays what it is where there is room");

		// what was asked or opened for one setup is no other setup's: the check's findings, and the settings window's Save
		Check(ToolRunner.App.MainViewModel.StillShown(@"C:\Setups\A.json", @"c:\setups\a.JSON"), "the same setup's file, however it is spelled, is still the one shown");
		Check(!ToolRunner.App.MainViewModel.StillShown(@"C:\Setups\A.json", @"C:\Setups\B.json") && !ToolRunner.App.MainViewModel.StillShown(@"C:\Setups\A.json", null),
			"another setup picked, or none: what was asked for the first is not shown under it");
		var picked = Setup.FromJson(new JsonObject(), @"C:\Setups\B.json");
		Equal(null, ToolRunner.App.SettingsPanel.NotThisSetup(@"c:\setups\b.json", "B", picked), "settings opened for the setup that is picked are saved to it");
		var why = ToolRunner.App.SettingsPanel.NotThisSetup(@"C:\Setups\A.json", "First", picked) ?? "";
		Check(why.StartsWith("Not saved", StringComparison.Ordinal) && why.Contains("\"First\"", StringComparison.Ordinal) && why.Contains($"\"{picked.Name}\"", StringComparison.Ordinal),
			"settings opened for one setup are not saved while another is picked - both are named: " + why);
		Check((ToolRunner.App.SettingsPanel.NotThisSetup(@"C:\Setups\A.json", "First", null) ?? "").Contains("no setup", StringComparison.Ordinal), "nor while none is picked");

		// The window's own sizes: kept at once, written a moment later and never on the window's thread - and whatever is kept
		// is in the file once the program asks for it as it closes, the newest of each
		using var f = new FakeMo2();
		var kept = Path.Combine(f.Root, "window.json");
		ToolRunner.App.UiState.FileForTests = kept;
		try {
			ToolRunner.App.UiState.Set("test.width", 640);
			ToolRunner.App.UiState.Set("test.height", 480);
			Equal(640.0, ToolRunner.App.UiState.Number("test.width") ?? 0, "a size is kept the moment it is set");
			ToolRunner.App.UiState.Save();
			var first = JsonNode.Parse(File.ReadAllText(kept))!;
			Check(first["test.width"]?.GetValue<double>() == 640 && first["test.height"]?.GetValue<double>() == 480, "both sizes are in the file once it is written: " + first.ToJsonString());
			ToolRunner.App.UiState.Set("test.width", 800);
			ToolRunner.App.UiState.Save();
			Equal(800.0, JsonNode.Parse(File.ReadAllText(kept))!["test.width"]!.GetValue<double>(), "a later size replaces the earlier one in the file");
			var stamp = File.GetLastWriteTimeUtc(kept);
			ToolRunner.App.UiState.Save();
			Equal(stamp, File.GetLastWriteTimeUtc(kept), "with nothing new kept, nothing is written");
			// a write the disk refuses (here: a folder stands where the file goes) is not counted as written: the next write
			// takes it along
			File.Delete(kept);
			Directory.CreateDirectory(kept);
			ToolRunner.App.UiState.Set("test.width", 900);
			ToolRunner.App.UiState.Save();
			Check(ToolRunner.App.UiState.Unsaved, "a write that failed leaves what is kept still to be written");
			Directory.Delete(kept, true);
			ToolRunner.App.UiState.Save();
			Check(!ToolRunner.App.UiState.Unsaved && JsonNode.Parse(File.ReadAllText(kept))!["test.width"]!.GetValue<double>() == 900, "and the next write puts it in the file");
			// The closing window's last write is made on a worker: tried, and tried once more when the disk refused. After it
			// the program's end leaves the file alone - its own write would be made on the window's thread
			Check(!ToolRunner.App.UiState.WrittenAtClose, "(no window has made its last write yet)");
			File.Delete(kept);
			Directory.CreateDirectory(kept);
			ToolRunner.App.UiState.Set("test.width", 1000);
			ToolRunner.App.UiState.SaveAtClose();
			Check(ToolRunner.App.UiState.Unsaved && ToolRunner.App.UiState.WrittenAtClose, "a last write the disk refused twice leaves it unsaved, and the program's end is told that the window has done its part");
			Directory.Delete(kept, true);
			ToolRunner.App.UiState.FileForTests = kept;
			Check(!ToolRunner.App.UiState.WrittenAtClose, "(a new start begins without it)");
			ToolRunner.App.UiState.Set("test.width", 1100);
			ToolRunner.App.UiState.SaveAtClose();
			Check(!ToolRunner.App.UiState.Unsaved && ToolRunner.App.UiState.WrittenAtClose && JsonNode.Parse(File.ReadAllText(kept))!["test.width"]!.GetValue<double>() == 1100,
				"a last write that goes through is in the file");
			// The program's data removed: a removal that left something behind changes nothing; after one that took all of it,
			// what the closing window still sets is kept in memory only - written, it would make the data folder again
			ToolRunner.App.UiState.FileForTests = kept;
			Check(!ToolRunner.App.UiState.RemoveWith(() => false), "a removal that did not take everything says so");
			ToolRunner.App.UiState.Set("test.width", 1200);
			ToolRunner.App.UiState.Save();
			Equal(1200.0, JsonNode.Parse(File.ReadAllText(kept))!["test.width"]!.GetValue<double>(), "and leaves the window's own writes as they were");
			Check(ToolRunner.App.UiState.RemoveWith(() => {
				File.Delete(kept);
				return true;
			}) && !ToolRunner.App.UiState.Unsaved, "a removal that took everything leaves nothing to write");
			ToolRunner.App.UiState.Set("test.height", 700);
			ToolRunner.App.UiState.SaveAtClose();
			Check(!File.Exists(kept) && !ToolRunner.App.UiState.Unsaved, "what the closing window sets after it is never written: the removed data stays removed");
			Equal(700.0, ToolRunner.App.UiState.Number("test.height") ?? 0, "(it is still kept for as long as the program runs)");
			// ... and the window lets go of its watch on the runs folder for a removal (MainWindow.WatchRuns): Windows moves no
			// folder - to the Recycle Bin or anywhere - while a folder inside it is watched
			var watched = Path.Combine(f.Root, "Data watched");
			Directory.CreateDirectory(Path.Combine(watched, "Runs"));
			using (var watch = new ToolRunner.App.RunsWatch(Path.Combine(watched, "Runs"), () => { })) {
				// (the watch must be on, or this proves nothing)
				Check(watch.Watching, "the runs folder is watched");
				var refused = false;
				try {
					Directory.Move(watched, watched + " moved");
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					refused = true;
				}
				Check(refused, "a data folder whose runs folder is watched is not moved");
			}
			Directory.Move(watched, watched + " moved");
			Check(Directory.Exists(watched + " moved") && !Directory.Exists(watched), "with the watch let go it is");
		} finally {
			ToolRunner.App.UiState.FileForTests = null;
		}
	}

	// A setup's file is written with the file as it stood copied aside first - disk work alone, which the window does away
	// from its own thread. A write that cannot be made says why and changes nothing
	static void T_ASetupIsWrittenWithACopyOfHowItStood()
	{
		using var f = new FakeMo2();
		var file = Path.Combine(f.Root, "Setups", "My Setup.json");
		var copies = Path.Combine(f.Root, "Setup backups");
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		Equal<string?>(null, Setup.WriteKeepingACopy(file, "first", copies), "a new setup is written");
		Check(File.ReadAllText(file) == "first" && Directory.GetFiles(copies).Length == 0, "with nothing to copy aside yet");
		Equal<string?>(null, Setup.WriteKeepingACopy(file, "second", copies), "written again");
		var kept = Directory.GetFiles(copies);
		Check(File.ReadAllText(file) == "second" && kept.Length == 1 && File.ReadAllText(kept[0]) == "first" && Path.GetFileName(kept[0]).StartsWith("My Setup 20", StringComparison.Ordinal),
			"the file as it stood is kept under the setup's name and the time");
		// two more saves, as fast as they come (most often inside one second): each keeps the file as it stood before it -
		// a copy is never written over by the next one
		Equal<string?>(null, Setup.WriteKeepingACopy(file, "second b", copies), "written again at once");
		Equal<string?>(null, Setup.WriteKeepingACopy(file, "second c", copies), "and once more");
		Equal("first,second,second b", string.Join(",", Directory.GetFiles(copies).Select(File.ReadAllText).Order(StringComparer.Ordinal)), "every save's copy is there");
		// only the newest copies of that setup stay; another setup's copies are not its to tidy
		for (var i = 0; i < Setup.CopiesKept + 5; ++i) {
			File.WriteAllText(Path.Combine(copies, $"My Setup 2026-01-{1 + i:00} 10-00-00.json"), "old");
		}
		File.WriteAllText(Path.Combine(copies, "Other Setup 2026-01-01 10-00-00.json"), "theirs");
		Equal<string?>(null, Setup.WriteKeepingACopy(file, "third", copies), "written once more");
		Equal(Setup.CopiesKept, Directory.GetFiles(copies, "My Setup *.json").Length, "the newest copies of this setup are kept, the older ones tidied away");
		Check(File.Exists(Path.Combine(copies, "Other Setup 2026-01-01 10-00-00.json")), "another setup's copies stay");
		// a write that cannot be made says why, and the setup's file is as it was
		Check(Setup.WriteKeepingACopy(Path.Combine(file, "inside a file.json"), "x", copies) is { Length: > 0 }, "a setup that cannot be written says why");
		Equal("third", File.ReadAllText(file), "and nothing else is changed");
		// More saves within ONE second than copies are kept: once the tidy has taken that second's oldest away, the next
		// copy is numbered past the highest still there, never into the freed name - under it, it read as the oldest and was
		// the next one tidied away, and the version just replaced had no copy at all
		var burst = Path.Combine(f.Root, "Burst backups");
		var oneSecond = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Local);
		File.WriteAllText(file, "v0");
		for (var i = 1; i <= Setup.CopiesKept + 2; ++i) {
			Equal<string?>(null, Setup.WriteKeepingACopy(file, $"v{i}", burst, oneSecond), $"(save {i} within that second)");
		}
		var keptNow = Directory.GetFiles(burst).Select(File.ReadAllText).ToHashSet(StringComparer.Ordinal);
		Equal(Setup.CopiesKept, keptNow.Count, "the newest copies of that second are kept");
		Check(keptNow.Contains($"v{Setup.CopiesKept + 1}") && keptNow.Contains($"v{Setup.CopiesKept}") && !keptNow.Contains("v0") && !keptNow.Contains("v1"),
			"the version each save replaced has its copy, the last one too; the oldest two went: " + string.Join(",", keptNow.Order(StringComparer.Ordinal)));
		// The PC's clock set back: every copy there reads newer than the one this save makes. That one still stays - the
		// version a save replaced always has its copy - with the newest of the others
		var back = Path.Combine(f.Root, "Clock backups");
		Directory.CreateDirectory(back);
		for (var i = 0; i < Setup.CopiesKept; ++i) {
			File.WriteAllText(Path.Combine(back, $"My Setup 2027-01-{1 + i:00} 10-00-00.json"), "later");
		}
		File.WriteAllText(file, "before the clock went back");
		Equal<string?>(null, Setup.WriteKeepingACopy(file, "after", back, new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Local)), "written with the clock set back");
		var backNow = Directory.GetFiles(back, "My Setup *.json");
		Equal(Setup.CopiesKept, backNow.Length, "as many copies are kept as ever");
		Check(backNow.Any(p => File.ReadAllText(p) == "before the clock went back"), "and the copy of the version this save replaced is one of them");
	}

	// a tool that never appeared keeps Mod Organizer's whole log since the request: every line after the place the request was made,
	// the whole log when it was started again meanwhile, the last lines only when there are many
	static void T_AFailedStartKeepsMo2sWholeLog()
	{
		var file = Path.Combine(Path.GetTempPath(), "Tool Runner test mo2 log " + Guid.NewGuid().ToString("N") + ".log");
		try {
			File.WriteAllText(file, "[I] before the request\r\n");
			var at = new FileInfo(file).Length;
			File.AppendAllText(file, "[I] RootBuilder: build complete\r\n[E] bridge: MO2 is starting\r\n");
			Equal("[I] RootBuilder: build complete|[E] bridge: MO2 is starting", string.Join("|", Mo2Control.RawLinesSince(file, at)), "every line since, info lines too");
			Equal(3, Mo2Control.RawLinesSince(file, at + 10_000).Count, "a log started again meanwhile: the whole of it");
			Equal("[E] bridge: MO2 is starting", string.Join("|", Mo2Control.RawLinesSince(file, 0, 1)), "only the last lines when there are many");
			Equal(0, Mo2Control.RawLinesSince(file + ".none", 0).Count, "no log, no lines");
		} finally {
			File.Delete(file);
		}
	}

	// a tool whose program lives in a mod switched off in the profile (Mod Organizer cannot start it from there - measured on
	// Landscape Fixer): that mod is switched on for the tool's run and off again after; one holding a plugin is left to
	// the player and named; a program outside the mods folder, or in a mod that is on, changes nothing
	static void T_AToolInASwitchedOffModIsSwitchedOnForItsRun()
	{
		using var f = new FakeMo2();
		f.Mod("Tool Mod");
		File.WriteAllText(Path.Combine(f.Mods, "Tool Mod", "tool.exe"), "");
		f.Mod("Plugin Tool", ("PluginTool.esp", false, []));
		File.WriteAllText(Path.Combine(f.Mods, "Plugin Tool", "tool2.exe"), "");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "-Tool Mod\r\n-Plugin Tool\r\n" + f.Text("modlist.txt"));
		string Bin(string a_mod, string a_exe) => Path.Combine(f.Mods, a_mod, a_exe).Replace('\\', '/');
		var ini = Path.Combine(f.Instance, "ModOrganizer.ini");
		File.WriteAllText(ini, File.ReadAllText(ini).Replace("[customExecutables]\r\nsize=1\r\n", "[customExecutables]\r\nsize=3\r\n" +
			$"2\\title=Tool\r\n2\\binary={Bin("Tool Mod", "tool.exe")}\r\n2\\arguments=\r\n3\\title=Tool2\r\n3\\binary={Bin("Plugin Tool", "tool2.exe")}\r\n3\\arguments=\r\n",
			StringComparison.Ordinal));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var p = ProfileOf(f);
		Equal("Tool Mod", StepKit.ToolMod(mo2, p, "Tool"), "a program inside a switched-off mod");
		Equal<string?>(null, StepKit.ToolMod(mo2, p, "SKSE"), "a program outside the mods folder");
		Equal<string?>(null, StepKit.ToolMod(mo2, p, "No Such Tool"), "an executable Mod Organizer does not have");
		var setup = f.WriteSetup(("pandora", new JsonObject()));
		var j = new Journal { Id = "tool mod", Folder = Path.Combine(Runner.RunsDir(f.Data), "0000 tool mod"), SetupFile = setup.File, SetupName = setup.Name, Profile = "Test", Started = DateTime.Now };
		Directory.CreateDirectory(j.Folder);
		using var log = new RunLog(j.Folder);
		var ctx = new RunContext { Setup = setup, Mo2 = mo2, Profile = p, Control = new Mo2Control(mo2, "Test", log), Log = log, Journal = j,
			Cancel = CancellationToken.None, BackupFolder = Path.Combine(f.Backups, "tool mod") };
		using (StepKit.ToolModOn(ctx, new StepSettings(new JsonObject { ["executable"] = "Tool" }))) {
			Check(p.IsOn("Tool Mod"), "on for the tool's run");
			Equal<string?>(null, StepKit.ToolMod(mo2, p, "Tool"), "(and then no longer a switched-off mod)");
		}
		Check(!p.IsOn("Tool Mod"), "off again after");
		var refused = "";
		try {
			StepKit.ToolModOn(ctx, new StepSettings(new JsonObject { ["executable"] = "Tool2" }))?.Dispose();
		} catch (StepFailedException e) {
			refused = e.Message;
		}
		Check(refused.Contains("\"Plugin Tool\"", StringComparison.Ordinal) && !p.IsOn("Plugin Tool"), "a tool mod with a plugin is named and left off: " + refused);
		Equal<Toggle?>(null, StepKit.ToolModOn(ctx, new StepSettings(new JsonObject { ["executable"] = "SKSE" })), "nothing to switch for a program outside the mods folder");
		Equal("Plugin Tool", string.Join(",", StepKit.ToolMods(ctx, new StepSettings(new JsonObject { ["executable"] = "Tool2", ["enable_during"] = new JsonArray("Plugin Tool") }))),
			"a mod the setup itself names for the step is its own choice, plugin or not - and still switched on, for the steps that do not read that list");
	}

	// a save unchanged since the last run's copy shares that copy's file; a changed one gets its own
	static void T_UnchangedSavesAreSharedBetweenBackups()
	{
		using var f = new FakeMo2();
		File.WriteAllText(Path.Combine(f.ProfileDir, "settings.ini"), "[General]\r\nLocalSaves=true\r\n");
		var saves = InGame.SavesDir(ProfileOf(f));
		Directory.CreateDirectory(saves);
		File.WriteAllText(Path.Combine(saves, "Old.ess"), "unchanged");
		File.WriteAllText(Path.Combine(saves, "New.ess"), "changed since");
		var earlier = Path.Combine(f.Backups, "2026-01-01 00-00-00 Test", InGame.SavesCopyFolder);
		Directory.CreateDirectory(earlier);
		File.Copy(Path.Combine(saves, "Old.ess"), Path.Combine(earlier, "Old.ess"));
		File.WriteAllText(Path.Combine(earlier, "New.ess"), "older words");
		f.WriteSetup(("cleansave", []));
		var ctx = StepContext(f, "cleansave");
		using var ctxLog = ctx.Log;
		var copy = InGame.CopySaves(ctx) ?? throw new InvalidOperationException("nothing copied");
		Equal("unchanged", File.ReadAllText(Path.Combine(copy, "Old.ess")), "the unchanged save is there");
		Equal("changed since", File.ReadAllText(Path.Combine(copy, "New.ess")), "the changed one as it is now");
		File.AppendAllText(Path.Combine(earlier, "Old.ess"), "!");
		Equal("unchanged!", File.ReadAllText(Path.Combine(copy, "Old.ess")), "one file under two names");
		Equal("changed since", File.ReadAllText(Path.Combine(copy, "New.ess")), "the changed one has its own");
		Directory.Delete(Path.GetDirectoryName(earlier)!, true);
		Equal("unchanged!", File.ReadAllText(Path.Combine(copy, "Old.ess")), "the older backup going takes nothing from this one");
	}

	// "Learn its settings" cut off with a tool's mod switched on: the next run puts that right before it reads the profile
	static void T_ALearnCutOffLeavesNoModOn()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => { }));
		var setup = f.WriteSetup(("landscapefixer", []));
		File.WriteAllText(ToolSettings.SwitchRecord(setup), "Replaced");
		Check(ProfileOf(f).IsOn("Replaced"), "(left on)");
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		Check(!ProfileOf(f).IsOn("Replaced"), "off again, and it stays off after the put-back");
		Check(!File.Exists(ToolSettings.SwitchRecord(setup)), "the record is gone");
		Check(ProfileOf(f).IsOn("Base Mod"), "nothing else moved");
		// a Stop while the tool may be open under the mod keeps it on; a Stop before the tool was started, or no Stop, puts it back
		Check(ToolSettings.KeepsModOn(true, true), "stopped with the tool started: the mod stays on, on record");
		Check(!ToolSettings.KeepsModOn(false, true), "stopped before the tool was started: the mod goes back");
		Check(!ToolSettings.KeepsModOn(true, false) && !ToolSettings.KeepsModOn(false, false), "no Stop: the mod goes back");
	}

	// before the first step that loads a save the player's saves are copied into the run's backup - saves and co-saves,
	// nothing else, once a run, the original untouched; the setup can switch it off; no room, no folder, a Stop or a copy that cannot be
	// made never stops the run or leaves a part behind
	static void T_TheSavesAreCopiedBeforeAnInGameStep()
	{
		using var f = new FakeMo2();
		File.WriteAllText(Path.Combine(f.ProfileDir, "settings.ini"), "[General]\r\nLocalSaves=true\r\n");
		var p = ProfileOf(f);
		var saves = InGame.SavesDir(p);
		Directory.CreateDirectory(saves);
		File.WriteAllText(Path.Combine(saves, "Save1.ess"), "save");
		File.WriteAllText(Path.Combine(saves, "Save1.skse"), "co-save");
		File.WriteAllText(Path.Combine(saves, "notes.txt"), "not a save");
		var setup = f.WriteSetup(("cleansave", new JsonObject()));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var j = new Journal { Id = "saves", Folder = Path.Combine(Runner.RunsDir(f.Data), "0000 saves"), SetupFile = setup.File, SetupName = setup.Name, Profile = "Test", Started = DateTime.Now };
		Directory.CreateDirectory(j.Folder);
		using var log = new RunLog(j.Folder);
		var backup = Path.Combine(f.Backups, "saves run");
		var ctx = new RunContext { Setup = setup, Mo2 = mo2, Profile = p, Control = new Mo2Control(mo2, "Test", log), Log = log, Journal = j,
			Cancel = CancellationToken.None, BackupFolder = backup };
		var copy = InGame.CopySaves(ctx);
		Check(copy != null && File.Exists(Path.Combine(copy, "Save1.ess")) && File.Exists(Path.Combine(copy, "Save1.skse")), "the saves and their co-saves are copied");
		Check(!File.Exists(Path.Combine(copy!, "notes.txt")), "nothing else");
		Check(File.Exists(Path.Combine(saves, "Save1.ess")), "the original stays");
		Check(!Directory.Exists(copy + ".part"), "no part left");
		File.WriteAllText(Path.Combine(saves, "Save2.ess"), "made in the run's game");
		Equal(copy, InGame.CopySaves(ctx), "once a run");
		Check(!File.Exists(Path.Combine(copy!, "Save2.ess")), "a later save is no part of the copy");
		setup.Root["copySaves"] = false;
		Directory.Delete(copy!, true);
		Equal<string?>(null, InGame.CopySaves(ctx), "switched off in the setup: nothing copied");
		setup.Root["copySaves"] = true;
		// not enough room beside what the run needs: said, nothing copied, and the run goes on
		var keepFree = InGame.FreeBytes;
		try {
			InGame.FreeBytes = _ => 0;
			Equal<string?>(null, InGame.CopySaves(ctx), "no room: no copy");
			Check(!Directory.Exists(Path.Combine(backup, InGame.SavesCopyFolder + ".part")), "no part left");
		} finally {
			InGame.FreeBytes = keepFree;
		}
		// a Stop during the copy leaves no part behind
		using var stop = new CancellationTokenSource();
		stop.Cancel();
		var stopping = new RunContext { Setup = setup, Mo2 = mo2, Profile = p, Control = ctx.Control, Log = log, Journal = j, Cancel = stop.Token, BackupFolder = backup };
		Throws<OperationCanceledException>(() => InGame.CopySaves(stopping), "a Stop ends the copy");
		Check(!Directory.Exists(Path.Combine(backup, InGame.SavesCopyFolder + ".part")), "and leaves no part behind");
		// the backup folder cannot be made (a file stands where it should be): said, and the run goes on
		Directory.Delete(backup, true);
		File.WriteAllText(backup, "a file in the way");
		Equal<string?>(null, InGame.CopySaves(ctx), "a copy that cannot be made never stops the run");
		File.Delete(backup);
		// no saves folder: said, and the run goes on
		Directory.Delete(saves, true);
		Equal<string?>(null, InGame.CopySaves(ctx), "no saves folder: nothing to copy");
		Check(Files.ReadSharedText(log.TextPath!).Contains("no saves folder", StringComparison.Ordinal), "and the log says so");
	}

	// Steam's own record of the game says whether it is updating it (refused), has an update waiting (a warning)
	// or is done (nothing); a copy outside a steamapps folder, or with no record, has nothing to read
	static void T_SteamUpdatingTheGameIsSeen()
	{
		var root = Path.Combine(Path.GetTempPath(), "Tool Runner test steam " + Guid.NewGuid().ToString("N"));
		var game = Path.Combine(root, "steamapps", "common", "Skyrim Special Edition");
		Directory.CreateDirectory(game);
		var manifest = Path.Combine(root, "steamapps", "appmanifest_489830.acf");
		try {
			Equal(0, SystemChecks.SteamUpdate(game, true).Count(), "no record: nothing to read");
			void State(int a_flags) => File.WriteAllText(manifest, "\"AppState\"\n{\n\t\"appid\"\t\t\"489830\"\n\t\"installdir\"\t\t\"Skyrim Special Edition\"\n\t\"StateFlags\"\t\t\"" + a_flags + "\"\n}\n");
			State(4);
			Equal(0, SystemChecks.SteamUpdate(game, true).Count(), "fully installed: nothing to say");
			State(1048580);
			Check(SystemChecks.SteamUpdate(game, true).Single().Blocking, "downloading: the run does not start");
			var closed = SystemChecks.SteamUpdate(game, false).Single();
			Check(!closed.Blocking && closed.Text.Contains("half written", StringComparison.Ordinal), "Steam closed in the middle of it: a warning that says so");
			State(1026);
			Check(!SystemChecks.SteamUpdate(game, true).Single().Blocking, "an update queued: a warning");
			State(1542);
			Check(!SystemChecks.SteamUpdate(game, true).Single().Blocking, "an update paused on purpose: a warning, the run may start");
			State(6);
			Check(!SystemChecks.SteamUpdate(game, true).Single().Blocking, "an update waiting: a warning");
			Equal(0, SystemChecks.SteamUpdate(Path.Combine(root, "Game Root"), true).Count(), "a copy outside steamapps: nothing to read");
			Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "Other Copy"));
			Equal(0, SystemChecks.SteamUpdate(Path.Combine(root, "steamapps", "common", "Other Copy"), true).Count(), "another folder in the same library: the record is not of it");
			Equal(1, SystemChecks.SteamUpdate(game + Path.DirectorySeparatorChar, true).Count(), "a trailing slash reads the same (the update still waiting)");
		} finally {
			Directory.Delete(root, true);
		}
	}

	// what holds a teleport - a beast form or a mount that cannot be left stops the step in the end; a kill move, a
	// conversation, a menu or a scene are waited out and go on, so a wrong reading never fails a run; nothing held = no wait
	static void T_WhatHoldsATeleportIsKnown()
	{
		InGame.PlayerState S(bool a_mounted = false, bool a_beast = false, bool a_menu = false, bool a_controlsOff = false, bool a_busy = false) =>
			new(a_mounted, a_beast, a_menu, a_controlsOff, a_busy);
		Check(InGame.WhatHoldsTeleport(S()) == null, "free to go");
		Check(InGame.WhatHoldsTeleport(S(a_beast: true)) is { Stop: true }, "a beast form stops in the end");
		Check(InGame.WhatHoldsTeleport(S(a_mounted: true)) is { Stop: true }, "a mount that cannot be left stops in the end");
		Check(InGame.WhatHoldsTeleport(S(a_menu: true)) is { Stop: false }, "a conversation or menu is waited out and goes on");
		Check(InGame.WhatHoldsTeleport(S(a_controlsOff: true)) is { Stop: false }, "a scene too");
		Check(InGame.WhatHoldsTeleport(S(a_busy: true)) is { Stop: false }, "a kill move or bleeding out too");
		Check(InGame.WhatHoldsTeleport(S(a_beast: true, a_menu: true))?.Why.Contains("werewolf", StringComparison.Ordinal) == true, "the most serious is named");
		Check(InGame.IsBeastRace("0x000CDD84", null), "the werewolf race");
		Check(InGame.IsBeastRace("0x0200283a", "0x0200283A"), "the vampire lord race as the game resolved it, any letter case");
		Check(!InGame.IsBeastRace("0x00013746", "0x0200283A") && !InGame.IsBeastRace("0x0300283A", "0x0200283A") && !InGame.IsBeastRace(null, null),
			"a normal race, the same low digits at another slot, or nothing are no beast");
		// the wait itself
		var keep = (InGame.TeleportWait, InGame.TeleportShortWait, InGame.TeleportPoll, InGame.DismountEvery);
		try {
			InGame.TeleportWait = InGame.TeleportShortWait = TimeSpan.FromMilliseconds(30);
			InGame.TeleportPoll = TimeSpan.FromMilliseconds(5);
			InGame.DismountEvery = TimeSpan.FromHours(1);
			var asked = 0;
			void Dismount() => ++asked;
			Equal<string?>(null, InGame.WaitUntilFree(() => S(), Dismount, RunLog.Null, CancellationToken.None), "nothing holds it: go on");
			var reads = 0;
			Equal<string?>(null, InGame.WaitUntilFree(() => S(a_mounted: ++reads == 1), Dismount, RunLog.Null, CancellationToken.None), "off the mount: go on");
			Equal(1, asked, "asked to get off once");
			Check(InGame.WaitUntilFree(() => S(a_beast: true), Dismount, RunLog.Null, CancellationToken.None)?.Contains("werewolf", StringComparison.Ordinal) == true, "a form that does not end: the step stops, named");
			asked = 0;
			Check(InGame.WaitUntilFree(() => S(a_mounted: true), Dismount, RunLog.Null, CancellationToken.None)?.Contains("mount", StringComparison.Ordinal) == true, "a mount that cannot be left: the same");
			Equal(1, asked, "and it is not asked again and again");
			Equal<string?>(null, InGame.WaitUntilFree(() => S(a_menu: true), Dismount, RunLog.Null, CancellationToken.None), "a menu that does not close: waited out, then on");
			// holds that keep taking turns end too
			InGame.TeleportWait = InGame.TeleportShortWait = TimeSpan.FromMilliseconds(200);
			var turn = 0;
			var turns = System.Diagnostics.Stopwatch.StartNew();
			InGame.WaitUntilFree(() => ++turn % 2 == 0 ? S(a_menu: true) : S(a_mounted: true), Dismount, RunLog.Null, CancellationToken.None);
			Check(turns.Elapsed >= InGame.TeleportWait * 2, "only the wait's own end stops holds that take turns");
			// a form that begins just before that end still gets its own time
			InGame.TeleportWait = InGame.TeleportShortWait = TimeSpan.FromMilliseconds(500);
			var flip = 0;
			var late = System.Diagnostics.Stopwatch.StartNew();
			Equal<string?>(null, InGame.WaitUntilFree(() => late.ElapsedMilliseconds < 900 ? (++flip % 2 == 0 ? S(a_menu: true) : S(a_controlsOff: true)) : late.ElapsedMilliseconds < 1200 ? S(a_beast: true) : S(), Dismount, RunLog.Null, CancellationToken.None), "it ends in time: go on");
			// each hold has its own limit: a menu after a long form still gets its time
			InGame.TeleportWait = TimeSpan.FromSeconds(5);
			InGame.TeleportShortWait = TimeSpan.FromMilliseconds(30);
			var clock = System.Diagnostics.Stopwatch.StartNew();
			Equal<string?>(null, InGame.WaitUntilFree(() => clock.ElapsedMilliseconds < 60 ? S(a_beast: true) : clock.ElapsedMilliseconds < 80 ? S(a_menu: true) : S(), Dismount, RunLog.Null, CancellationToken.None), "both end: go on");
			Check(clock.ElapsedMilliseconds >= 80, "the menu was waited out, not passed on the form's time");
			using var stop = new CancellationTokenSource();
			stop.Cancel();
			Throws<OperationCanceledException>(() => InGame.WaitUntilFree(() => S(a_menu: true), Dismount, RunLog.Null, stop.Token), "a Stop ends the wait");
		} finally {
			(InGame.TeleportWait, InGame.TeleportShortWait, InGame.TeleportPoll, InGame.DismountEvery) = keep;
		}
	}

	static void T_TheRealGameArchiveHoldsItsWords()
	{
		if (Environment.GetEnvironmentVariable("ToolRunnerRealMo2") is not { Length: > 0 } exe) {
			Console.WriteLine("      (skipped: ToolRunnerRealMo2 not set)");
			return;
		}
		var game = Mo2Instance.Load(exe, Environment.GetEnvironmentVariable("ToolRunnerRealBase")).GameDir;
		var texts = GameWords.TranslationsIn(Path.Combine(game, "Data", "Skyrim - Interface.bsa"));
		Check(texts.Count >= 8, $"every language's file: {texts.Count}");
		var w = GameWords.Of(texts, []);
		Equal(("answered NO", 1), DevBench.DecideBox("This save relies on Creations you have already downloaded but are currently inactive:\nMod.esp\nActivate now?", ["Ja", "Nein"], 2, w), "the German buttons, off the game's own file");
		Check(w.AsksNo("You can also search for missing content in the Creations menu."), "a box's own English text, off that file");
	}

	// a save that did not happen is said in the player's words, and names the way forward
	static void T_ASaveThatDidNotHappenIsSaid()
	{
		var first = InGame.SaveNotMade("Tool Runner - clean save", false);
		Check(first.Contains("clean save", StringComparison.Ordinal) && first.Contains("may be blocking saves", StringComparison.Ordinal) && first.Contains("Force the save", StringComparison.Ordinal),
			"names the save, the likely cause and the setting: " + first);
		var forced = InGame.SaveNotMade("x", true);
		Check(forced.Contains("switched back on", StringComparison.Ordinal) && !forced.Contains("Force the save", StringComparison.Ordinal) && forced.Contains("guided", StringComparison.Ordinal),
			"after the force: what is left to do, not the same advice again: " + forced);
		Check(InGame.SaveWait <= TimeSpan.FromMinutes(5), "a blocked save is said, never waited on for long");
	}

	// the old timed runs' tasks are looked for once per data folder (Windows' task list took 35 s at every start); only a clean pass
	// leaves the marker
	static void T_OldTimedRunsAreLookedForOnce()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner test once " + Guid.NewGuid().ToString("N"));
		var looks = 0;
		try {
			Schedule.RemoveLeftoversOnce(dir, () => { ++looks; return false; });
			Equal(1, looks, "the first start looks");
			Check(!File.Exists(Path.Combine(dir, Schedule.ClearedMarker)), "a pass that was not clean leaves no marker");
			Schedule.RemoveLeftoversOnce(dir, () => { ++looks; return true; });
			Equal(1, looks, "but a command is not held up by it again the same day");
			File.SetLastWriteTimeUtc(Path.Combine(dir, Schedule.TriedMarker), DateTime.UtcNow - Schedule.TryEvery - TimeSpan.FromMinutes(1));
			Schedule.RemoveLeftoversOnce(dir, () => { ++looks; return true; });
			Equal(2, looks, "a day later it looks again");
			Check(File.Exists(Path.Combine(dir, Schedule.ClearedMarker)), "a clean pass leaves it");
			Schedule.RemoveLeftoversOnce(dir, () => { ++looks; return true; });
			Equal(2, looks, "and no start looks after that");
		} finally {
			if (Directory.Exists(dir)) {
				Directory.Delete(dir, true);
			}
		}
	}

	// only what each tool's own document says - DynDOLOD.esp and Occlusion.esp last, Occlusion.esp after DynDOLOD.esp,
	// PG_1.esp last but for those, DynDOLOD.esm the last master; Synthesis, PGPatcher.esp and Landscape Fixer have no documented place;
	// always a warning
	static void T_OutputPluginsAreCheckedAgainstTheirDocuments()
	{
		List<Issue> C(params string[] a_order) => PluginPlaces.Check(a_order);
		Equal(0, C("Skyrim.esm", "DynDOLOD.esm", "Some.esp", "Merged Patch.esp", "Synthesis.esp", "PG_1.esp", "DynDOLOD.esp", "Occlusion.esp").Count, "an order by the documents is clean");
		Equal(0, C("Some.esp", "Other.esp").Count, "none of them ticked: nothing to say");
		Equal(0, C("PGPatcher.esp", "LandscapeFixer.esp", "Synthesis.esp", "Some.esp").Count, "no documented place: never warned");
		Check(C("DynDOLOD.esp", "Occlusion.esp").Count == 0 && C("Occlusion.esp", "DynDOLOD.esp").Single().Text.Contains("right after", StringComparison.Ordinal), "Occlusion.esp after DynDOLOD.esp");
		var late = C("DynDOLOD.esp", "Late Mod.esp");
		Check(late.Single().Text.Contains("Late Mod.esp", StringComparison.Ordinal) && !late[0].Blocking, "a plugin after DynDOLOD.esp is named, and only warned about");
		Check(C("PG_1.esp", "Late Mod.esp").Single().Text.Contains("PGPatcher", StringComparison.Ordinal), "a plugin after PG_1.esp");
		Equal(0, C("PG_1.esp", "PG_2.esp", "DynDOLOD.esp", "Occlusion.esp").Count, "PG plugins and the DynDOLOD outputs after them are fine");
		Check(C("PG_1.esp", "PG_2.esp", "Merged Patch.esp").Single().Text.StartsWith("Merged Patch.esp", StringComparison.Ordinal), "a plugin after every PG plugin that must load last: named once");
		Check(C("DynDOLOD.esm", "Late.esm").Single().Text.Contains("Late.esm", StringComparison.Ordinal), "a master after DynDOLOD.esm");
		Check(C("DynDOLOD.esm", "Late.esl").Single().Text.Contains("Late.esl", StringComparison.Ordinal), "a light master after it too");
		Equal(0, C("Early.esm", "DynDOLOD.esm", "Some.esp").Count, "plugins after DynDOLOD.esm are fine");
	}

	// a standard Windows button by its control id, a Qt box's by where it stands; a Mod Organizer in another language
	// answers only what came after the close request
	static void T_ButtonsAreKnownByWhatTheyDo()
	{
		Equal("Yes", Uia.StandardName("6"), "a Windows box's Yes, in any language");
		Equal("No", Uia.StandardName("CommandButton_7"), "a task dialog's No");
		Equal("OK", Uia.StandardName("1"), "OK");
		Equal<string?>(null, Uia.StandardName("9"), "not a standard button");
		Equal<string?>(null, Uia.StandardName("0"), "nor this");
		Equal<string?>(null, Uia.StandardName("mainWindow.closeButton"), "a Qt name is no id");
		Equal<string?>(null, Uia.StandardName(""), "no id at all");
		// the id names a standard button only when the button's words are Windows' own for it, here or in English
		static string Spanish(string a_standard) => a_standard switch { "Yes" => "Sí", "No" => "No", "OK" => "Aceptar", _ => "" };
		Equal("No", Uia.StandardName("CommandButton_7", "&No", Spanish), "its English word, the access mark left out");
		Equal("Yes", Uia.StandardName("6", "Sí", Spanish), "Windows' own word for it");
		Equal<string?>(null, Uia.StandardName("CommandButton_7", "Restart", Spanish), "a program's own button under No's id is no No");
		Equal<string?>(null, Uia.StandardName("1", "Zip and Exit", Spanish), "nor one under OK's id an OK");
		Equal<string?>(null, Uia.StandardName("6", "Ja", Spanish), "a word of another language than Windows' is the program's own");
		Equal<string?>(null, Uia.StandardName("9", "OK", Spanish), "no standard id, whatever it reads");
		Equal("1|0", string.Join("|", Uia.ReadingOrder([(100.0, 50.0), (10.0, 50.0)])), "a row is read left to right");
		Equal("0|1", string.Join("|", Uia.ReadingOrder([(10.0, 50.0), (10.0, 90.0)])), "a column top to bottom");
		Check(!Mo2Control.SpeaksOther("en", "de-DE") && !Mo2Control.SpeaksOther("", "en-US"), "English by its setting, or by Windows when it has none");
		Check(Mo2Control.SpeaksOther("de", "en-US") && Mo2Control.SpeaksOther("", "fr-FR"), "another language by its setting first");
		Check(!Mo2Control.CloseBox("Wirklich beenden?", 2), "a question it cannot read is left for the player");
		Check(Mo2Control.IsJava("javaw.exe") && !Mo2Control.IsJava("xEdit.exe"), "Java is any Java program's");
	}

	// the game's own words for its load boxes, from a game archive laid out as the game's is
	static void T_GameBoxesAreReadInItsOwnLanguage()
	{
		const string German = "$Yes\tJa\r\n$No\tNein\r\n$SaveGameMissingContentActivate\tJetzt aktivieren?\r\n$SaveGameInactiveContent\tDieser Spielstand braucht inaktive Inhalte:\r\n$Other\tEtwas anderes\r\n$SaveGameUnownedContinue\t\r\n";
		var w = GameWords.Of([German], ["Dieser Spielstand braucht fehlende Inhalte. Weiter laden?"]);
		Equal(("answered NO", 1), DevBench.DecideBox("Dieser Spielstand braucht inaktive Inhalte:\nMod.esp\nJetzt aktivieren?", ["Ja", "Nein"], 2, w), "the first box: No, in the game's words");
		Equal(("answered YES", 0), DevBench.DecideBox("Dieser Spielstand braucht fehlende Inhalte. Weiter laden?", ["Ja", "Nein"], 2, w), "the second box: Yes");
		Equal<string?>(null, DevBench.DecideBox("Etwas anderes", ["Ja", "Nein"], 2, w).Answer, "any other box is not answered");
		Equal<string?>(null, DevBench.DecideBox("Dieser Spielstand braucht fehlende Inhalte. Weiter laden?", ["Oui", "Non"], 2, w).Answer, "nor one whose buttons it cannot read");
		Equal(("answered NO", 1), DevBench.DecideBox("Some content is missing, search the Creations menu?", ["Yes", "No"], 2, w), "English still reads");
		Equal(0, GameWords.Of([], []).HasAskYes, "nothing read: nothing known");
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner test words " + Guid.NewGuid().ToString("N"));
		try {
			Directory.CreateDirectory(Path.Combine(dir, "Data"));
			var other = "something else"u8.ToArray();
			var text = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes(German)).ToArray();
			var names = System.Text.Encoding.Latin1.GetBytes("other.txt\0translate_german.txt\0");
			const int FolderName = 11;
			var dataAt = 36 + 24 + FolderName + 2 * 16 + names.Length;
			using (var o = new BinaryWriter(File.Create(Path.Combine(dir, "Data", "Skyrim - Interface.bsa")))) {
				o.Write(0x00415342u);
				o.Write(105u);
				o.Write(36u);
				o.Write(3u);
				o.Write(1);
				o.Write(2);
				o.Write(FolderName - 1);
				o.Write(names.Length);
				o.Write(0u);
				o.Write(1UL);
				o.Write(2);
				o.Write(0u);
				o.Write(0UL);
				o.Write((byte)(FolderName - 1));
				o.Write(System.Text.Encoding.Latin1.GetBytes("interface\0"));
				o.Write(1UL);
				o.Write((uint)other.Length);
				o.Write((uint)dataAt);
				o.Write(2UL);
				o.Write((uint)text.Length);
				o.Write((uint)(dataAt + other.Length));
				o.Write(names);
				o.Write(other);
				o.Write(text);
			}
			var read = GameWords.Read(dir, s => s == "sSaveGameContentIsMissing" ? "Dieser Spielstand braucht fehlende Inhalte. Weiter laden?" : null);
			Equal(1, read.HasAskYes, "the game setting that answered");
			Equal(("answered NO", 1), DevBench.DecideBox("Jetzt aktivieren?", ["Ja", "Nein"], 2, read), "read out of the archive");
			Equal(0, GameWords.Read(Path.Combine(dir, "none"), _ => null).HasAskYes, "a game folder with no archive is no failure");
			File.WriteAllBytes(Path.Combine(dir, "Data", "Skyrim - Interface.bsa"), [1, 2, 3]);
			Equal(0, GameWords.Read(dir, _ => null).HasAskYes, "nor one that does not read");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// Root Builder setups are waited for, and our add-on goes in beside Root Builder, never without it
	static void T_RootBuilderIsWaitedFor()
	{
		using var f = new FakeMo2();
		var plain = Mo2Instance.Load(f.Exe, f.Instance);
		Check(!plain.RootBuilderBuilds && !plain.RootBuilderDeployed(), "no Root Builder: nothing to wait for");
		Equal(RootBuilderAddOn.State.NoRootBuilder, RootBuilderAddOn.StateOf(plain), "and no add-on to offer");
		var plugins = Path.Combine(Path.GetDirectoryName(f.Exe)!, "plugins");
		Directory.CreateDirectory(Path.Combine(plugins, "rootbuilder"));
		Check(Mo2Instance.Load(f.Exe, f.Instance).RootBuilderBuilds, "nothing in the ini: Root Builder's own default, its automatic build on");
		var iniFile = Path.Combine(f.Instance, "ModOrganizer.ini");
		var noPlugins = File.ReadAllText(iniFile);
		File.WriteAllText(iniFile, noPlugins + "[Plugins]\r\nRootBuilder\\autobuild=false\r\n");
		Check(!Mo2Instance.Load(f.Exe, f.Instance).RootBuilderBuilds, "its automatic build off: it builds only when the player asks");
		File.WriteAllText(iniFile, noPlugins + "[Plugins]\r\nRootBuilder\\enabled=false\r\n");
		Check(!Mo2Instance.Load(f.Exe, f.Instance).RootBuilderBuilds, "the plugin switched off in Mod Organizer: nothing builds");
		File.WriteAllText(iniFile, noPlugins + "[Plugins]\r\nRootBuilder\\autobuild=true\r\n");
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Check(mo2.RootBuilderBuilds, "its automatic build on");
		Check(!mo2.RootBuilderDeployed(), "nothing built yet");
		var data = Path.Combine(plugins, "data", "RootBuilder", "game", "1_6_1170_0");
		Directory.CreateDirectory(data);
		File.WriteAllText(Path.Combine(data, "BuildData.json"), "{}");
		Check(mo2.RootBuilderDeployed(), "built: its files are in the game folder");
		var keep = (Mo2Control.RootBuilderWait, Mo2Control.RootBuilderLook);
		try {
			Mo2Control.RootBuilderLook = TimeSpan.FromMilliseconds(5);
			var reads = 0;
			Check(Mo2Control.WaitForClear(() => ++reads < 3, CancellationToken.None), "waited until they are out");
			Equal(3, reads, "looked until then");
			Mo2Control.RootBuilderWait = TimeSpan.FromMilliseconds(30);
			Check(!Mo2Control.WaitForClear(() => true, CancellationToken.None), "and not for ever");
			using var stop = new CancellationTokenSource();
			stop.Cancel();
			Throws<OperationCanceledException>(() => Mo2Control.WaitForClear(() => true, stop.Token), "a Stop ends the wait");
		} finally {
			(Mo2Control.RootBuilderWait, Mo2Control.RootBuilderLook) = keep;
		}
		Equal(RootBuilderAddOn.State.NotAdded, RootBuilderAddOn.StateOf(mo2), "the add-on on offer");
		RootBuilderAddOn.Add(mo2);
		Equal(RootBuilderAddOn.State.Current, RootBuilderAddOn.StateOf(mo2), "added");
		var added = Path.Combine(plugins, "rootbuilderprofiles");
		Check(File.ReadAllText(Path.Combine(added, "__init__.py")).Contains("def createPlugin", StringComparison.Ordinal), "under the name Mod Organizer loads");
		Check(File.ReadAllText(Path.Combine(added, "core.py")).Contains("\"Local\\\\" + Brand.Name + " run\"", StringComparison.Ordinal), "it knows this program's run lock by its real name");
		File.AppendAllText(Path.Combine(added, "core.py"), "# changed\n");
		Equal(RootBuilderAddOn.State.Different, RootBuilderAddOn.StateOf(mo2), "another copy of it is told apart");
		RootBuilderAddOn.Add(mo2);
		Equal(RootBuilderAddOn.State.Current, RootBuilderAddOn.StateOf(mo2), "and brought level");
	}

	// AnvilLOD takes DynDOLOD's place: found like any tool, its step made by New setup - switched off where DynDOLOD is there
	// too, each keeping the other's output off -, and the two never run together
	static void T_AnvilLodIsAStepANewSetupMakes()
	{
		using var f = new FakeMo2();
		foreach (var (mod, exe) in new[] { ("DynDOLOD Tool", "DynDOLODx64.exe"), ("Anvil Tool", "AnvilLOD.exe") }) {
			Directory.CreateDirectory(Path.Combine(f.Mods, mod));
			File.WriteAllText(Path.Combine(f.Mods, mod, exe), "");
		}
		var found = ToolFinder.Find(Mo2Instance.Load(f.Exe, f.Instance), [f.Mods]);
		Equal(ToolFinder.Found.NotListed, found.Single(r => r.Tool.Step == "anvillod").State, "AnvilLOD is one of the tools looked for");
		// with AnvilLOD alone in the list its step is there, with its program and its output - and off: it reads DynDOLOD's rule
		// files, whose folder is not known without DynDOLOD on the list (switched on, every start of the setup stopped on it)
		ToolFinder.AddFound(Mo2Instance.Load(f.Exe, f.Instance), found.Where(r => r.Tool.Step == "anvillod"));
		var alone = SetupMaker.Make(f.Exe, f.Instance, "Test", "Anvil only", f.Data);
		var only = alone.Steps.Single(s => s.Key == "anvillod");
		Check(!only.On && Files.SamePath(Setup.Str(only.Settings, "exe")!, Path.Combine(f.Mods, "Anvil Tool", "AnvilLOD.exe")) && Setup.Str(only.Settings, "output_mod") == "AnvilLOD Output - Test",
			"its step, off, with its program and its output: " + only.Settings.ToJsonString());
		Check(only.Settings["dyndolod"] == null, "DynDOLOD's folder is left for the player to set, never written as nothing");
		Check(alone.Steps.All(s => s.Key != "dyndolod") && Setup.List(only.Settings, "replaces").Count == 0, "no DynDOLOD: nothing for it to keep off");
		// with both, DynDOLOD's step is on and AnvilLOD's is there, off; each names the other's output
		ToolFinder.AddFound(Mo2Instance.Load(f.Exe, f.Instance), ToolFinder.Find(Mo2Instance.Load(f.Exe, f.Instance), [f.Mods]));
		var both = SetupMaker.Make(f.Exe, f.Instance, "Test", "Both", f.Data);
		var (dyn, anvil) = (both.Steps.Single(s => s.Key == "dyndolod"), both.Steps.Single(s => s.Key == "anvillod"));
		Check(dyn.On && !anvil.On, "DynDOLOD's step is the one switched on");
		Equal("AnvilLOD Output - Test", Setup.List(dyn.Settings, "replaces").Single(), "DynDOLOD keeps AnvilLOD's output off");
		Equal("DynDOLOD Output - Test", Setup.List(anvil.Settings, "replaces").Single(), "and AnvilLOD DynDOLOD's");
		Check(Files.SamePath(Setup.Str(anvil.Settings, "dyndolod")!, Path.Combine(f.Mods, "DynDOLOD Tool")), "AnvilLOD is told where DynDOLOD's rule files are");
		var order = both.Steps.Select(s => s.Key).ToList();
		Check(order.IndexOf("dyndolod") < order.IndexOf("anvillod") && StepCatalog.OrderWarnings(order).Count == 0, "in an order the tools' own rules allow: " + string.Join(", ", order));
		// the two switched on together are refused, with what to do
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var refused = Preflight.Run(both, mo2, ["dyndolod", "anvillod"]).Where(i => i.Blocking && i.Text.Contains("both switched on", StringComparison.Ordinal)).ToList();
		Check(refused.Count == 1 && RunnerErrors.Find(refused[0].Text)?.Code == "TR-323", "both at once is refused: " + string.Join(" | ", refused.Select(i => i.Text)));
		Check(!Preflight.Run(both, mo2, ["dyndolod"]).Any(i => i.Text.Contains("both switched on", StringComparison.Ordinal)), "one of them is not");
	}

	// the tools on the PC named against Mod Organizer's list, the found ones added to it with every other line left alone
	static void T_TheFirstStartHelperFindsTools()
	{
		using var f = new FakeMo2();
		foreach (var (mod, exe) in new[] { ("DynDOLOD Tool", "DynDOLODx64.exe"), ("Far, away", "Synthesis.exe"), ("Tool Mod", Path.Combine("sub", "folder", "PGPatcher.exe")) }) {
			Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(f.Mods, mod, exe))!);
			File.WriteAllText(Path.Combine(f.Mods, mod, exe), "");
		}
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var iniBefore = File.ReadAllText(mo2.IniPath);
		var rows = ToolFinder.Find(mo2, [f.Mods]);
		ToolFinder.Row Of(List<ToolFinder.Row> a_rows, string a_step) => a_rows.Single(r => r.Tool.Step == a_step);
		Equal(ToolFinder.Found.NotListed, Of(rows, "dyndolod").State, "on the PC, not in the list");
		Equal(ToolFinder.Found.NotListed, Of(rows, "pgpatcher").State, "a tool in a mod's sub folder too");
		Equal(ToolFinder.Found.Missing, Of(rows, "pandora").State, "not on the PC");
		Check(Of(rows, "pandora").Tool.Page.StartsWith("https://", StringComparison.Ordinal) && Of(rows, "pandora").Tool.Where != "", "says where to get it and where it goes");
		Equal(3, ToolFinder.AddFound(mo2, rows).Count, "the found ones added");
		Check(File.ReadAllText(ToolFinder.BackupOf(mo2)) == iniBefore, "the list as it was is kept beside it");
		var after = Mo2Instance.Load(f.Exe, f.Instance);
		Equal("-sse", after.Executable("DynDOLOD")!.Arguments, "the xEdit family names its game");
		Check(Files.SamePath(after.Executable("Synthesis")!.Binary, Path.Combine(f.Mods, "Far, away", "Synthesis.exe")), "a path with a comma reads back whole");
		Check(after.Executable("SKSE") != null && after.SelectedProfile == "Test", "what was there stays");
		Check(File.ReadAllText(after.IniPath).Contains("[Settings]", StringComparison.Ordinal), "and the sections after it");
		var again = ToolFinder.Find(after, [f.Mods]);
		Equal(ToolFinder.Found.Listed, Of(again, "dyndolod").State, "now in the list");
		Equal(0, ToolFinder.AddFound(after, again).Count, "nothing to add twice");
		// a title already taken by something else; a list with no section yet
		Directory.CreateDirectory(Path.Combine(f.Mods, "Other"));
		File.WriteAllText(Path.Combine(f.Mods, "Other", "TexGenx64.exe"), "");
		File.WriteAllText(after.IniPath, File.ReadAllText(after.IniPath).Replace(@"title=SKSE", @"title=TexGen", StringComparison.Ordinal));
		var clash = Mo2Instance.Load(f.Exe, f.Instance);
		Equal("TexGen (2)", ToolFinder.AddFound(clash, ToolFinder.Find(clash, [f.Mods])).Single(), "a title in use is left to its owner");
		var made = ToolFinder.WithExecutable("[General]\nx=1\n", "A", Path.Combine(f.Root, "a.exe"), "");
		var read = Mo2Instance.ReadQtIni(made);
		Check(read["General"]["x"] == "1" && read["customExecutables"]["size"] == "1" && read["customExecutables"][@"1\title"] == "A", "a list that was not there is made: " + made);
	}

	// the Tools menu entry points at the program that added it, and a run never waits for that program under Mod Organizer
	static void T_TheToolsMenuEntryOpensThisProgram()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		const string Program = @"D:\Tools\Tool Runner.exe";
		Check(!Mo2MenuEntry.IsCurrent(mo2, Program), "not there yet");
		Mo2MenuEntry.Add(mo2, Program);
		Check(Mo2MenuEntry.IsCurrent(mo2, Program), "added");
		var file = Path.Combine(Path.GetDirectoryName(f.Exe)!, "plugins", "toolrunner.py");
		var text = File.ReadAllText(file);
		Check(text.Contains("PROGRAM = r\"" + Program + "\"", StringComparison.Ordinal) && !text.Contains("@PROGRAM@", StringComparison.Ordinal), "it points at the program");
		Check(text.Contains("\"--for-profile\"", StringComparison.Ordinal) && text.Contains("\"--mo2\"", StringComparison.Ordinal) && text.Contains("\"--base\"", StringComparison.Ordinal), "and asks for what the window takes");
		Check(!Mo2MenuEntry.IsCurrent(mo2, @"E:\Moved\Tool Runner.exe"), "a program moved since is offered it again");
		Check(Mo2Control.IsThisProgram(Brand.Name + ".exe"), "this program under Mod Organizer is not one a run waits for");
		Check(!Mo2Control.IsThisProgram("SSEEdit.exe"), "another program is");
	}

	// the settings of the small, muted window, for the step only: back after it, after a failure too; off, nothing changes
	static void T_TheQuietGameWindowGoesBack()
	{
		const string Prefs = "[AudioMenu]\r\nfAudioMasterVolume=1.0000\r\nfVal=1\r\n[Display]\r\nbBorderless=1\r\nbFull Screen=1\r\niSize H=1080\r\niSize W=1920\r\nother=keep\r\n[General]\r\nsLanguage=ENGLISH\r\n";
		var changed = QuietGame.GamePrefs(Prefs);
		var read = Mo2Instance.ReadQtIni(changed);
		Equal("0", read["Display"]["bFull Screen"], "windowed");
		Equal("0", read["Display"]["bBorderless"], "with its border");
		Equal("960", read["Display"]["iSize W"], "small");
		Equal("1", read["General"]["bAlwaysActive"], "and running behind other windows");
		Equal("0.0000", read["AudioMenu"]["fAudioMasterVolume"], "muted");
		Equal("keep", read["Display"]["other"], "the rest as it was");
		Equal(Prefs.Split("\r\n").Length + 1, changed.Split("\r\n").Length, "only the missing line was added");
		var tweaks = QuietGame.DisplayTweaks("[Render]\n#    true  - Exclusive fullscreen mode\nFullscreen = true\nBorderless = true\nBorderlessUpscale = false\n#Resolution=1920x1080\nResolutionScale=0.75\n[Other]\nResolution=keep\n");
		Check(tweaks.Contains("\nFullscreen=false\n", StringComparison.Ordinal) && tweaks.Contains("\nBorderless=false\n", StringComparison.Ordinal), "its windowed mode: " + tweaks);
		Check(tweaks.Contains("\nBorderlessUpscale = false\n", StringComparison.Ordinal) && tweaks.Contains("#    true  - Exclusive fullscreen mode", StringComparison.Ordinal), "another setting and its comment are left alone");
		Check(tweaks.Contains("\nResolution=960x540\n", StringComparison.Ordinal) && tweaks.Contains("\n#ResolutionScale=0.75\n", StringComparison.Ordinal), "the size, and the scale that would apply over it taken out");
		Check(tweaks.Contains("[Other]\nResolution=keep\n", StringComparison.Ordinal), "only in its own section");
		Equal("[X]\nk=1\n", QuietGame.Set("[X]\nk=0\n", "X", "k", "1"), "an active line is set");
		Equal("[X]\na=b\n[Y]\nk=1\n", QuietGame.Set("[X]\na=b\n", "Y", "k", "1"), "a section that was not there");

		using var f = new FakeMo2();
		var prefs = Path.Combine(f.ProfileDir, "skyrimprefs.ini");
		File.WriteAllText(prefs, Prefs);
		File.WriteAllText(Path.Combine(f.ProfileDir, "settings.ini"), "[General]\r\nLocalSettings=true\r\n");
		var tweakFile = Path.Combine(f.Mods, "Base Mod", "SKSE", "Plugins", "SSEDisplayTweaks.ini");
		Directory.CreateDirectory(Path.GetDirectoryName(tweakFile)!);
		const string TweaksBefore = "[Render]\r\nFullscreen = true\r\n";
		File.WriteAllText(tweakFile, TweaksBefore);
		// the player's own file, which SSE Display Tweaks reads over the mod's
		var customFile = Path.Combine(f.Overwrite, "SKSE", "Plugins", "SSEDisplayTweaks_Custom.ini");
		Directory.CreateDirectory(Path.GetDirectoryName(customFile)!);
		const string CustomBefore = "[Render]\r\nBorderless = true\r\nResolution = 2560x1440\r\n";
		File.WriteAllText(customFile, CustomBefore);
		var seen = "";
		var fail = false;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			QuietGame.Apply(ctx);
			QuietGame.Apply(ctx);
			seen = File.ReadAllText(prefs) + File.ReadAllText(tweakFile) + File.ReadAllText(customFile);
			if (fail) {
				throw new StepFailedException("a failure");
			}
		}));
		var setup = f.WriteSetup(("landscapefixer", new JsonObject()));
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "off: the run finished");
		Check(seen == Prefs + TweaksBefore + CustomBefore, "off: nothing changes");
		foreach (var on in new[] { "LocalSettings=true", "LocalSettings = true", "  localsettings=TRUE " }) {
			Check(QuietGame.IsLocalSettings(on), "the setting however it is spaced: " + on);
		}
		Check(!QuietGame.IsLocalSettings("LocalSettings=false") && !QuietGame.IsLocalSettings("NotLocalSettings=true") && !QuietGame.IsLocalSettings("[General]"), "and no other line");
		Check(!QuietGame.IsGog(f.Game), "a game with no GOG library is not GOG's, whatever its folder");
		File.WriteAllText(Path.Combine(f.Game, "Galaxy64.dll"), "");
		Check(QuietGame.IsGog(f.Game), "one with it is, whatever its folder");
		setup.Root["quietGame"] = true;
		setup.Save(f.SetupFile);
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "on: the run finished");
		Check(seen.Contains("iSize W=960", StringComparison.Ordinal) && seen.Contains("Fullscreen=false", StringComparison.Ordinal), "on: the step sees the small window");
		Check(seen.Contains("Borderless=false\r\nResolution=960x540", StringComparison.Ordinal), "the player's own _Custom file, which wins over the mod's, is set too: " + seen);
		Equal(Prefs, File.ReadAllText(prefs), "and the game's settings go back");
		Equal(TweaksBefore, File.ReadAllText(tweakFile), "and the mod's");
		Equal(CustomBefore, File.ReadAllText(customFile), "and the player's own");
		fail = true;
		Check(Runner.Run(new RunRequest(f.SetupFile, f.Data)).State != RunState.Finished, "a step that fails");
		Check(seen.Contains("iSize W=960", StringComparison.Ordinal), "had them changed");
		Equal(Prefs, File.ReadAllText(prefs), "and they go back too");
		Equal(TweaksBefore, File.ReadAllText(tweakFile), "the mod's as well");
	}

	// an add-on file is read and checked in plain words, goes where it says in the order, and a file changed since is asked about
	static void T_AnAddOnFileIsCheckedAndPlaced()
	{
		const string Good = """
			{ "schema": 1, "name": "My Patcher", "author": "A Modder", "page": "https://example.com/p", "program": "tools/MyPatcher.exe", "arguments": "--auto",
			  "output": "My Patcher Output", "minutes": 30, "makes": "MyPatch.esp", "after": "synthesis" }
			""";
		Equal(0, AddOns.Problems(Good).Count, "a right file: " + string.Join("; ", AddOns.Problems(Good)));
		bool Says(string a_file, string a_word) => AddOns.Problems(a_file).Any(p => p.Contains(a_word, StringComparison.Ordinal));
		Check(Says("not json", "not valid JSON"), "not JSON");
		Check(Says("[1]", "one JSON object"), "not an object");
		Check(Says("""{ "schema": 2 }""", "\"schema\""), "another schema");
		Check(Says("""{ "schema": 1 }""", "\"program\" is missing"), "a key left out");
		Check(Says(Good.Replace("\"page\"", "\"pgae\"", StringComparison.Ordinal), "typing mistake"), "a key misspelt");
		Check(Says(Good.Replace("tools/MyPatcher.exe", "../other/x.exe", StringComparison.Ordinal), "inside your mod"), "a program outside the mod");
		Check(Says(Good.Replace("tools/MyPatcher.exe", "C:/x.exe", StringComparison.Ordinal), "inside your mod"), "a whole path");
		Check(Says(Good.Replace("tools/MyPatcher.exe", "tools/run.bat", StringComparison.Ordinal), "inside your mod"), "not a program");
		Check(Says(Good.Replace("\"synthesis\"", "\"nothing\"", StringComparison.Ordinal), "which is no step"), "a step that does not exist");
		Check(Says(Good.Replace("My Patcher Output", "My:Output", StringComparison.Ordinal), "good folder name"), "an output that makes no folder");
		Check(Says(Good.Replace("\"minutes\": 30", "\"minutes\": 0", StringComparison.Ordinal), "\"minutes\""), "a time that is no time");
		Check(Says(Good.Replace("https://example.com/p", "http://example.com/p", StringComparison.Ordinal), "https"), "a page that is not https");

		using var f = new FakeMo2();
		var mod = Path.Combine(f.Mods, "Base Mod");
		var file = Path.Combine(mod, AddOns.FolderInMod, "patcher.json");
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, Good);
		Check(AddOns.Problems(Good, mod).Any(p => p.Contains("not in the mod", StringComparison.Ordinal)), "the program is looked for in the mod");
		Directory.CreateDirectory(Path.Combine(mod, "tools"));
		File.WriteAllText(Path.Combine(mod, "tools", "MyPatcher.exe"), "");
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var found = AddOns.Find(mo2, ProfileOf(f)).Single();
		var addOn = found.AddOn ?? throw new InvalidOperationException(string.Join("; ", found.Problems));
		Check(addOn.Says.Contains(addOn.ProgramPath, StringComparison.Ordinal) && addOn.Says.Contains("--auto", StringComparison.Ordinal) && addOn.Says.Contains("My Patcher Output", StringComparison.Ordinal), "the question says what it will start and where it writes");
		var setup = f.WriteSetup(("synthesis", new JsonObject()), ("pgpatcher", new JsonObject()), ("texgen", new JsonObject()), ("playsave", new JsonObject()));
		AddOns.AddToSetup(setup.Root, addOn, "Test");
		Equal("synthesis|addon:My Patcher|pgpatcher|texgen|playsave", string.Join("|", setup.Steps.Select(s => s.Key)), "after the step it names");
		Equal("My Patcher Output - Test", setup.Steps[1].Settings["output_mod"]?.ToString(), "its output, named as every output");
		Equal("My Patcher", StepCatalog.Name(addOn.Key), "its name in the window");
		Check(StepCatalog.Known(addOn.Key) && StepRegistry.For(addOn.Key) is AddOnStep, "a step like any other");
		Check(setup.Problems().Count == 0, "the setup reads: " + string.Join("; ", setup.Problems()));
		AddOns.AddToSetup(setup.Root, addOn with { After = null }, "Test");
		Equal("synthesis|pgpatcher|addon:My Patcher|texgen|playsave", string.Join("|", setup.Steps.Select(s => s.Key)), "saying nothing: before the LOD tools, and never twice");
		// allowed, then changed
		var check = new CheckContext { Setup = setup, Mo2 = mo2, Profile = ProfileOf(f), States = ProfileOf(f).States() };
		var settings = new StepSettings(setup.Steps[2].Settings);
		var step = new AddOnStep(addOn.Key);
		Equal(0, step.Check(check, settings).Count(i => i.Blocking), "allowed: nothing blocks");
		File.AppendAllText(file, " ");
		Check(step.Check(check, settings).Single(i => i.Blocking).Text.Contains("changed since", StringComparison.Ordinal), "a file changed since is asked about again");
		Check(AddOns.InSetup(setup, addOn) && AddOns.Allowed(setup, addOn), "the old copy was allowed");
		var changedNow = AddOns.Find(mo2, ProfileOf(f)).Single().AddOn!;
		Check(AddOns.InSetup(setup, changedNow) && !AddOns.Allowed(setup, changedNow), "so Settings offers it again");
		// the real step: what starts is what was allowed
		f.WriteSetup((addOn.Key, setup.Steps[2].Settings.DeepClone().AsObject()));
		var changedCtx = StepContext(f, addOn.Key);
		using (changedCtx.Log) {
			Throws<StepFailedException>(() => step.Run(changedCtx, settings), "changed after the pre-check: it does not start");
		}
		var other = f.WriteSetup(("synthesis", new JsonObject()), ("pgpatcher", new JsonObject()), ("texgen", new JsonObject()), ("playsave", new JsonObject()));
		// an output name that is one of the player's mods
		Check(AddOns.OutputTaken(mo2, other, changedNow) == null, "a name nobody has");
		f.Mod("My Patcher Output - Test", ("Theirs.esp", false, []));
		Equal("My Patcher Output - Test", AddOns.OutputTaken(mo2, other, changedNow), "a mod of the player's is named, never replaced");
		// its two places cannot both hold
		Check(AddOns.AddToSetup(other.Root, changedNow with { After = "synthesis", Before = "pgpatcher" }, "Test") == null, "both can hold");
		Check(AddOns.AddToSetup(other.Root, changedNow with { After = "pgpatcher", Before = "synthesis" }, "Test") is { } clash && clash.Contains("the other way round", StringComparison.Ordinal), "both cannot: the player is told");
		Directory.Delete(Path.Combine(f.Mods, "My Patcher Output - Test"), true);
		AddOns.AddToSetup(setup.Root, changedNow, "Test");
		Check(AddOns.Allowed(setup, changedNow), "and allowing it again takes the new file");
		settings = new StepSettings(setup.Steps.Single(s => s.Key == addOn.Key).Settings);
		Equal(0, step.Check(check, settings).Count(i => i.Blocking), "nothing blocks then");
		File.AppendAllText(file, " ");
		setup.Root[AddOns.NoAskSetting] = true;
		Equal(0, step.Check(check, settings).Count(i => i.Blocking), "unless the player switched the question off");
	}

	// what the tool wrote goes into its own output mod; a tool that wrote nothing, or not what it promised, failed and its old output comes back
	static void T_AnAddOnsFilesGoIntoItsOwnMod()
	{
		using var f = new FakeMo2();
		File.WriteAllText(Path.Combine(f.Overwrite, "there before.ini"), "the player's");
		var mode = "good";
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			var before = Runner.OverwriteFiles(ctx);
			if (mode != "nothing") {
				Directory.CreateDirectory(Path.Combine(f.Overwrite, "Docs"));
				File.WriteAllText(Path.Combine(f.Overwrite, "Docs", "log.txt"), "log");
				File.WriteAllText(Path.Combine(f.Overwrite, mode == "good" ? "MyPatch.esp" : "Other.esp"), "plugin");
				Directory.CreateDirectory(Path.Combine(f.Overwrite, "Root"));
				File.WriteAllText(Path.Combine(f.Overwrite, "Root", "from the game folder.txt"), "Root Builder's");
			}
			AddOnStep.Collect(ctx, "synthesis", "Output A", "MyPatch.esp", before);
			ctx.SetAfter("Output A");
		}));
		f.WriteSetup(("synthesis", new JsonObject { ["output_mod"] = "Output A" }));
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "MyPatch.esp")) && File.Exists(Path.Combine(f.Mods, "Output A", "Docs", "log.txt")), "each file at its own place in its mod");
		Check(!File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "the old output left it");
		Check(!File.Exists(Path.Combine(f.Overwrite, "MyPatch.esp")), "nothing of it stays in overwrite");
		Check(File.Exists(Path.Combine(f.Overwrite, "there before.ini")), "what was there before stays");
		Check(File.Exists(Path.Combine(f.Overwrite, "Root", "from the game folder.txt")), "and what Root Builder cleared there is not the tool's");
		mode = "nothing";
		Check(Runner.Run(new RunRequest(f.SetupFile, f.Data)).Verdict.Contains("wrote nothing", StringComparison.Ordinal), "a tool that wrote nothing failed");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "MyPatch.esp")), "and the output stays");
		mode = "other";
		Check(Runner.Run(new RunRequest(f.SetupFile, f.Data)).Verdict.Contains("did not write MyPatch.esp", StringComparison.Ordinal), "nor one that did not write what it promised");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "MyPatch.esp")), "and the output stays");
	}

	// a tool newer than this release was proven with is a note, never a stop; what a run presses is looked for by its name, or its id
	static void T_AToolsWindowIsCheckedAgainstWhatARunPresses()
	{
		Equal<ToolRunner.Steps.Issue?>(null, ToolProof.Newer("synthesis", "0.36.6.0"), "the version it was proven with");
		Equal<ToolRunner.Steps.Issue?>(null, ToolProof.Newer("synthesis", "0.30"), "an older one is no news");
		Equal<ToolRunner.Steps.Issue?>(null, ToolProof.Newer("synthesis", ""), "one that cannot be read says nothing");
		Equal<ToolRunner.Steps.Issue?>(null, ToolProof.Newer("playsave", "9"), "a step with no tool");
		var newer = ToolProof.Newer("synthesis", "0.37.0");
		Check(newer is { Blocking: false } && newer.Text.Contains("0.37.0", StringComparison.Ordinal) && newer.Text.Contains("0.36.6", StringComparison.Ordinal), "a newer one is said, and only said");
		foreach (var k in ToolProof.ProvenWith.Keys) {
			Check(StepCatalog.Known(k), k + " is a step");
		}
		ToolProof.Look L(string a_step) => ToolProof.Looks.Single(l => l.Step == a_step);
		Equal<string?>(null, ToolProof.Missing(L("xlodgen"), [("Generate", "1", true)]), "its button there");
		Check(ToolProof.Missing(L("xlodgen"), [("Generate", "1", false)])?.Contains("Generate", StringComparison.Ordinal) == true, "a button that cannot be pressed does not count");
		Check(ToolProof.Missing(L("xlodgen"), [("Other", "1", true)])?.Contains("Other", StringComparison.Ordinal) == true, "what it shows instead is named");
		Check(ToolProof.Missing(L("xlodgen"), [])?.Contains("none", StringComparison.Ordinal) == true, "no buttons at all");
		Equal<string?>(null, ToolProof.Missing(L("synthesis"), [("", "GoButton", true)]), "Synthesis's by its id");
		Equal<string?>(null, ToolProof.Missing(L("landscapefixer"), [("Generieren", "", true)], ["Generieren"]), "a tool's own translation of it");
		Check(ToolProof.Looks.All(l => StepCatalog.Known(l.Step)), "every look is at a step");
	}

	// a language file's words reach what no binding fills, what it leaves out stays English, and one that does not read changes nothing
	static void T_TheWindowCanBeTranslated()
	{
		Exception? failed = null;
		var t = new Thread(() => {
			try {
				var bound = new System.Windows.Controls.TextBlock();
				System.Windows.Data.BindingOperations.SetBinding(bound, System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Now") { Source = new { Now = "Start" } });
				var button = new System.Windows.Controls.Button { Content = "Start", ToolTip = "Starts it" };
				System.Windows.Automation.AutomationProperties.SetName(button, "Start");
				var left = new System.Windows.Controls.TextBlock { Text = "Not in the file" };
				var panel = new System.Windows.Controls.StackPanel();
				panel.Children.Add(button);
				panel.Children.Add(left);
				panel.Children.Add(bound);
				panel.Children.Add(new System.Windows.Controls.ContentControl { Content = new System.Windows.Controls.TextBlock { Text = "Inside" } });
				Equal("Inside|Not in the file|Start|Starts it", string.Join("|", ToolRunner.App.WindowWords.Seen(panel)), "what a translator gets");
				ToolRunner.App.WindowWords.Use(new Dictionary<string, string> { ["Start"] = "Starten", ["Starts it"] = "Startet es", ["Inside"] = "Innen", ["Not in the file"] = " " });
				ToolRunner.App.WindowWords.Tree(panel);
				Equal("Starten", button.Content?.ToString(), "a button");
				Equal("Startet es", button.ToolTip?.ToString(), "its tool tip");
				Equal("Starten", System.Windows.Automation.AutomationProperties.GetName(button), "what a screen reader says");
				Equal("Not in the file", left.Text, "an empty translation stays English");
				Equal("Start", bound.Text, "what a binding fills is left alone");
				Equal("Inside|Not in the file|Start|Starts it", string.Join("|", ToolRunner.App.WindowWords.Seen(panel)), "a file made with a language in use keeps its English keys");
				var themed = new System.Windows.Controls.TextBlock();
				themed.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "SomeToken");
				themed.Resources["SomeToken"] = "Token";
				ToolRunner.App.WindowWords.Seen(themed);
				themed.Resources["SomeToken"] = "Another";
				Equal("Another", themed.Text, "a theme's text still follows its resource after the walk");
			} catch (Exception e) {
				failed = e;
			} finally {
				ToolRunner.App.WindowWords.Use(new Dictionary<string, string>());
			}
		});
		t.SetApartmentState(ApartmentState.STA);
		t.Start();
		t.Join();
		if (failed != null) {
			throw new InvalidOperationException("the language test: " + failed.Message, failed);
		}
		Check(ToolRunner.App.WindowWords.Read("not json", out var problem) == null && problem != "", "a file that does not read says why");
		Equal("Starten", ToolRunner.App.WindowWords.Read("{ \"Start\": \"Starten\", }", out _)?["Start"], "a file a person wrote reads");
	}

	// a question and its answer as files; no answer is said in time and leaves nothing behind
	static void T_TheCompanionAnswersOrIsPassedOver()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var folder = Path.Combine(Path.GetDirectoryName(f.Exe)!, "plugins", "data", "ToolRunner");
		Check(!Companion.Ask(mo2, "Test", "ping", TimeSpan.FromMilliseconds(150), CancellationToken.None), "nobody there: no answer");
		Equal(0, Directory.GetFiles(folder).Length, "and nothing left behind");
		using var stop = new CancellationTokenSource();
		var heard = new List<string>();
		var answering = Task.Run(() => {
			while (!stop.IsCancellationRequested) {
				foreach (var ask in Directory.GetFiles(folder, "ask *.txt")) {
					heard.Add(File.ReadAllText(ask));
					var id = Path.GetFileNameWithoutExtension(ask)["ask ".Length..];
					File.Delete(ask);
					File.WriteAllText(Path.Combine(folder, $"done {id}.txt"), "ok");
				}
				Thread.Sleep(10);
			}
		});
		Check(Companion.Ask(mo2, "Test", "refresh", TimeSpan.FromSeconds(20), CancellationToken.None), "it answers");
		stop.Cancel();
		answering.Wait();
		Equal($"refresh\n{mo2.BaseDir}\nTest", heard.Single(), "what was asked, and of which Mod Organizer");
		Equal(0, Directory.GetFiles(folder).Length, "and nothing left behind");
		var entry = Mo2MenuEntry.Text(@"D:\Tools\Tool Runner.exe");
		Check(entry.Contains("self._mine(base, profile)", StringComparison.Ordinal), "the plugin answers only for its own instance and profile");
		Check(entry.Contains("onNextRefresh", StringComparison.Ordinal) && entry.Contains("refresh(False)", StringComparison.Ordinal), "the plugin reads the lists back in, without saving its own over them");
		Check(!Mo2MenuEntry.Present(mo2), "no entry yet");
		Mo2MenuEntry.Add(mo2, @"D:\Tools\Tool Runner.exe");
		Check(Mo2MenuEntry.Present(mo2), "the entry is there");
		var control = new Mo2Control(mo2, "Test", RunLog.Null);
		Check(!control.HeldOpen, "Mod Organizer is not held open unless asked");
	}

	// each step's help is its tool's own page (never a guess), and the save steps get DynDOLOD's clean-save help
	static void T_EveryToolStepHasItsHelpPage()
	{
		foreach (var s in StepCatalog.All.Where(s => s.Key is not ("shaders" or "xlodgen"))) {
			var help = ToolRunner.App.SettingsPanel.HelpPage(s.Key);
			Check(help is { } h && h.Url.StartsWith("https://", StringComparison.Ordinal) && !h.Url.Contains("?tab=files", StringComparison.Ordinal), $"{s.Name} has its page: {help?.Url}");
		}
		Equal("https://dyndolod.info/Help/Clean-Save", ToolRunner.App.SettingsPanel.HelpPage("cleansave")?.Url, "the clean save's help");
	}

	// a game that closed under an in-game step names the crash log written since the run began - Crash Logger's
	// and Trainwreck's in the SKSE folder, .NET Script Framework's in overwrite - and never an older one
	static void T_AGameCrashNamesItsCrashLog()
	{
		using var f = new FakeMo2();
		var skse = Path.Combine(f.Root, "SKSE");
		var trainwreck = Path.Combine(skse, "Crashlogs");
		var nsf = Path.Combine(f.Overwrite, "NetScriptFramework", "Crash");
		foreach (var d in new[] { skse, trainwreck, nsf }) {
			Directory.CreateDirectory(d);
		}
		var since = DateTime.Now.AddMinutes(-10);
		var old = Path.Combine(skse, "crash-2026-01-01-00-00-00.log");
		File.WriteAllText(old, "old");
		File.SetLastWriteTime(old, since.AddHours(-1));
		Equal<string?>(null, InGame.CrashLog(skse, f.Overwrite, since), "a crash log from before the run is not this run's");
		Check(InGame.CrashNote(skse, f.Overwrite, since).Contains("no crash report", StringComparison.Ordinal), "said when there is none");
		var tw = Path.Combine(trainwreck, "crash-2026-11-07-21-00-00.log");
		File.WriteAllText(tw, "x");
		File.SetLastWriteTime(tw, since.AddMinutes(2));
		Equal<string?>(tw, InGame.CrashLog(skse, f.Overwrite, since), "Trainwreck's, in its own folder");
		var net = Path.Combine(nsf, "Crash_2026_11_07_21_05_00.txt");
		File.WriteAllText(net, "x");
		File.SetLastWriteTime(net, since.AddMinutes(5));
		Equal<string?>(net, InGame.CrashLog(skse, f.Overwrite, since), "the newest wins (.NET Script Framework's, in overwrite)");
		Check(InGame.CrashNote(skse, f.Overwrite, since).Contains(net, StringComparison.Ordinal), "the note names it");
		Check(InGame.CrashNote(skse, f.Overwrite, since).Contains(tw, StringComparison.Ordinal), "and the other crash logger's beside it");
	}

	// ---- the shader step on a profile with several shader mods ----

	// The cache the profile runs comes first, built with every ticked shader mod on; one that is switched off only when the
	// setup asks. The case: Bottled Shaders off, a test layer and Open Shaders both ticked (the layer lower in the pane).
	static void T_TheProfilesOwnShaderCacheComesFirst()
	{
		var bottled = new ShaderStep.Cache("Bottled Shaders", "Cache - Bottled", null);
		var layer = new ShaderStep.Cache("TEST layer", "Cache", null);
		var open = new ShaderStep.Cache("Open Shaders", "Cache - Open", "Open Settings");
		var gone = new ShaderStep.Cache("Not Installed", "Cache - Gone", null);
		List<ShaderStep.Cache> all = [bottled, layer, open, gone];
		var states = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase) {
			["Bottled Shaders"] = '-', ["TEST layer"] = '+', ["Open Shaders"] = '+', ["Open Settings"] = '+', ["Cache - Bottled"] = '+',
		};
		List<string> order = ["Bottled Shaders", "TEST layer", "Open Shaders", "Open Settings"];
		var (builds, notes) = ShaderStep.Plan(all, states, order, false, ["Extra On"], ["Extra Off"]);
		Equal(1, builds.Count, "one cache: the profile's own");
		var b = builds[0];
		Equal("TEST layer", b.Cache.ShaderMod, "into the cache of the ticked shader mod that wins (the first in modlist.txt)");
		Check(b.Runs, "it is the one the profile runs");
		Check(b.On.Contains("TEST layer") && b.On.Contains("Open Shaders") && b.On.Contains("Open Settings") && b.On.Contains("Extra On"), "every ticked shader mod stays on for it: " + string.Join(", ", b.On));
		Check(b.Off.Contains("Bottled Shaders") && b.Off.Contains("Extra Off") && new[] { "Cache - Bottled", "Cache", "Cache - Open" }.All(b.Off.Contains), "the one that is off, and every cache mod, off for it");
		Check(!b.Off.Contains("Open Shaders") && !b.Off.Contains("TEST layer"), "a ticked shader mod is never switched off under another");
		Check(b.Layers.Contains("Open Shaders") && b.Layers.Contains("Open Settings") && !b.Layers.Contains("TEST layer"), "the other ticked ones are its layers");
		Check(notes.Any(n => n.Contains("Bottled Shaders is switched off", StringComparison.Ordinal)), "the one left out is said: " + string.Join(" | ", notes));
		Check(notes.Any(n => n.Contains("Not Installed is not in this profile", StringComparison.Ordinal)), "and the one that is not installed");

		var (asked, _) = ShaderStep.Plan(all, states, order, true, [], []);
		Equal(2, asked.Count, "the setup asks for the switched-off one too");
		Check(asked[0].Runs && !asked[1].Runs && asked[1].Cache == bottled, "after the profile's own, never before it");
		Check(asked[1].On.SequenceEqual(["Bottled Shaders"]) && asked[1].Off.Contains("TEST layer") && asked[1].Off.Contains("Open Shaders"), "alone, the ticked ones off for it");

		states["TEST layer"] = '-';
		var (single, _) = ShaderStep.Plan(all, states, order, false, [], []);
		Check(single.Count == 1 && single[0].Cache == open && single[0].Layers.Count == 0 && single[0].Off.Contains("TEST layer"), "one ticked shader mod: its own cache, no layers");
		states["Open Shaders"] = '-';
		var (none, why) = ShaderStep.Plan(all, states, order, false, [], []);
		Check(none.Count == 0 && why.Any(n => n.Contains("no shader mod is ticked", StringComparison.Ordinal)), "nothing ticked: nothing built, and said");
	}

	// The end of a compile. In a real run the shader log was never found and, found, its last line read "remaining=549" five minutes
	// before the real end (Bottled Shaders writes that line only for a slow shader) - so the game itself is read.
	static void T_ACompileEndsWhenTheGameGoesQuiet()
	{
		var (min, quiet, idle) = (50, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(45));
		var slow = "[2026-11-10 12:07:56.657] [info] [46528] [ShaderCache.cpp:3537] [ShaderTiming] Very slow 12370ms | queue_wait=183805ms | remaining=549 | defines=1\n";
		var bottled = "[2026-11-10 12:04:00.425] [info] Loaded CommunityShaders 1-9-0-0\n" + slow + string.Concat(Enumerable.Repeat("[2026-11-10 12:13:38.804] [info] [ReverseZ.cpp:683] ReverseZ: camera\n", 400));
		Equal<int?>(549, InGame.RemainingIn(bottled), "its last count is read however many lines follow it");
		Equal<int?>(null, InGame.RemainingIn("Open Shaders 2.20.0\n"), "a log without such lines");
		Equal<int?>(7, InGame.RemainingIn("[12:00:00] [ShaderTiming] remaining=7\n[12:00:01] [ShaderTiming] remaining=99999999999\n"),
			"a count past what a number holds is no count: the one before it is the newest");
		ShaderStep.Look At(string a_log, int a_files, double a_sinceFile, double? a_cores, double a_peak, double a_low, bool a_sane = true, double a_sinceLog = 400, int? a_new = null) =>
			new(a_log, a_files, a_new ?? a_files, TimeSpan.FromSeconds(a_sinceFile), TimeSpan.FromSeconds(a_sinceLog), a_cores, a_peak, TimeSpan.FromSeconds(a_low), a_sane);

		var done = ShaderStep.Done(At(bottled, 3305, 50, 0.6, 13.2, 50), min, quiet, idle);
		Check(done is { Verified: true } && done.Value.Result.Contains("processor use fell from 13.2 to 0.6", StringComparison.Ordinal), "50 s after the last shader, not five minutes: " + done?.Result);
		Equal(null, ShaderStep.Done(At(bottled, 3305, 176, 12.0, 13.2, 0), min, quiet, idle), "a long gap with the processor busy is a slow shader, never the end");
		Equal(null, ShaderStep.Done(At(bottled, 3305, 50, 0.6, 13.2, 20), min, quiet, idle), "low for less than the idle time: not yet");
		Equal(null, ShaderStep.Done(At(bottled, 3305, 20, 0.6, 13.2, 50), min, quiet, idle), "a shader landed 20 s ago: not yet");
		Equal(null, ShaderStep.Done(At(bottled, 3305, 400, 0.6, 0.9, 400, a_sinceLog: 100), min, quiet, idle), "never seen compiling and the log still moving: no verdict from the processor");
		Equal(null, ShaderStep.Done(At(bottled, 30, 50, 0.6, 13.2, 50), min, quiet, idle), "too few shaders for a cache");
		Equal(null, ShaderStep.Done(At(bottled, 1700, 50, 0.6, 5.0, 50, a_new: 0), min, quiet, idle), "a restart that has only loaded so far (no shader of its own yet) is not a finished compile");
		Check(ShaderStep.Done(At(bottled, 3305, 400, 3.0, 13.2, 0), min, quiet, idle) is { Verified: false }, "a main menu that keeps the processor busy: the quiet fallback still ends it");
		Equal(null, ShaderStep.Done(At(bottled, 3305, 50, 0.6, 13.2, 50, a_sane: false), min, quiet, idle), "the shader mod never showed it runs");

		var zero = ShaderStep.Done(At(slow.Replace("remaining=549", "remaining=0", StringComparison.Ordinal), 3305, 31, 9.0, 13.2, 0), min, quiet, idle);
		Check(zero is { Verified: true } && zero.Value.Result.Contains("remaining=0", StringComparison.Ordinal), "the log's own remaining=0 comes first");
		var open = "[info] Shader compilation started (3169 tasks queued)\n";
		Equal(null, ShaderStep.Done(At(open, 3000, 60, 0.5, 14.0, 60), min, quiet, idle), "Open Shaders has an open compile: only its own line ends it");
		var said = ShaderStep.Done(At(open + "[info] Shader compilation completed: 3169/3169 tasks (0 failed) in 29m\n", 3169, 0, 14.0, 14.0, 0), min, quiet, idle);
		Check(said is { Verified: true } && said.Value.Result.StartsWith("[info] Shader compilation completed", StringComparison.Ordinal), "and that line ends it at once");

		Equal(null, ShaderStep.Done(At("", 3305, 200, null, 0, 0, a_sinceLog: 1e6), min, quiet, idle), "the processor cannot be read: the old quiet rule, not before its minutes");
		var byQuiet = ShaderStep.Done(At("", 3305, 301, null, 0, 0, a_sinceLog: 301), min, quiet, idle);
		Check(byQuiet is { Verified: false } && byQuiet.Value.Result.Contains("by quiet", StringComparison.Ordinal), "after them it ends unverified (built again next time)");
		Check(ShaderStep.IdleLine(13.2) == 2.0 && Math.Abs(ShaderStep.IdleLine(4.0) - 1.4) < 1e-9 && ShaderStep.IdleLine(1.0) == 0.75, "low = a third of the game's own peak, never above two cores");
	}

	// SKSE's log folder shown elsewhere by a Mod Organizer file-mapper plugin (a path with an apostrophe in a Python raw
	// string): the shader log and the crash logs are read where they really are
	static void T_LogsMappedElsewhereAreFound()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("shaders", new JsonObject()));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var usual = InGame.SkseLogs(setup, mo2);
		Check(InGame.LogFolders(setup, mo2).SequenceEqual([usual]), "no mapper: the usual folder alone");
		var real = Path.Combine(f.Root, "Real Logs");
		Directory.CreateDirectory(real);
		var plugins = Path.Combine(f.Instance, "plugins");
		Directory.CreateDirectory(plugins);
		File.WriteAllText(Path.Combine(plugins, "Other.py"), $"TARGET = r\"{real}\"\nclass X: pass  # SKSE\n");
		Check(InGame.LogFolders(setup, mo2).Count == 1, "a plugin that maps nothing is not read as one");
		File.WriteAllText(Path.Combine(plugins, "SKSELogRedirector.py"),
			$"import mobase\nTARGET = r\"{real}\"\nMISSING = 'C:\\\\no\\\\such folder'\nclass R(mobase.IPluginFileMapper):\n    def mappings(self):\n        return [\"SKSE\", \"Logs\"]\n");
		Check(InGame.LogFolders(setup, mo2).Count == 1, "a folder it names that holds no log is not taken for a log folder");
		File.WriteAllText(Path.Combine(real, "BottledShaders.log"), "x");
		var folders = InGame.LogFolders(setup, mo2);
		Check(folders.Count == 2 && folders[0] == usual && Files.SamePath(folders[1], real), "the folder the mapper names, after the usual one: " + string.Join(" | ", folders));
		File.AppendAllText(Path.Combine(plugins, "SKSELogRedirector.py"), $"ROOT = \"{Path.GetPathRoot(real)!.Replace("\\", "\\\\")}\"\n");
		Check(InGame.LogFolders(setup, mo2).Count == 2, "a drive's root is never one (every such folder is searched for crash logs)");
		Check(InGame.IsCrashReport("crash-2026-11-10-12-03-27.log") && InGame.IsCrashReport("crash_2026.log") && InGame.IsCrashReport("SkyrimDiag_Crash_20261110_120327_711_SkyrimDiagReport.txt") &&
			!InGame.IsCrashReport("SkyrimDiag_Crash_20261110_120327_711.dmp") && !InGame.IsCrashReport("SkyrimDiagHelper.previous.2.log"), "a crash report by its name, never a dump or a helper's own log");
		Equal<string?>(Path.Combine(real, "BottledShaders.log"), InGame.NewestShaderLog(folders), "the shader log is found there");
		var since = DateTime.Now.AddMinutes(-1);
		var crash = Path.Combine(real, "crash-2026-11-10-12-03-27.log");
		File.WriteAllText(crash, "x");
		Check(InGame.CrashNote(folders, f.Overwrite, since).Contains(crash, StringComparison.Ordinal), "and a crash log written there is named");
		setup.Root["skseLogs"] = Path.Combine(f.Root, "Named");
		Check(InGame.LogFolders(setup, mo2).SequenceEqual([Path.Combine(f.Root, "Named")]), "a folder the setup names is the only one read");
	}

	sealed class FakeGuide(Action<string, CancellationToken> a_ask) : IGuide
	{
		public void Ask(string a_step, string a_text, CancellationToken a_ct) => a_ask(a_text, a_ct);
	}

	// A crash while compiling is named as a crash with its report (Tullius CTD Logger's, in overwrite), and the game is
	// never started again silently: asked (the default), restarted or stopped as the setup says
	static void T_ACrashWhileCompilingIsNamedNeverRestartedSilently()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("shaders", new JsonObject()));
		var cache = new ShaderStep.Cache("Shader Mod", "Cache Mod", null);
		var asked = new List<string>();
		var ctx = StepContext(f, "shaders", a_guide: new FakeGuide((t, _) => asked.Add(t)));
		ctx.GameLaunched = DateTime.Now.AddMinutes(-5);
		var dir = Path.Combine(f.Overwrite, "SKSE", "Plugins", "Tullius Ctd Logs");
		Directory.CreateDirectory(dir);
		var old = Path.Combine(dir, "SkyrimDiag_Crash_20261109_010101_001_SkyrimDiagReport.txt");
		File.WriteAllText(old, "an earlier crash");
		File.SetLastWriteTime(old, DateTime.Now.AddHours(-9));
		StepSettings S(string a_how, double a_wait = 30) => new(new JsonObject { ["on_crash"] = a_how, ["crash_wait_min"] = a_wait });

		ShaderStep.GameEnded(ctx, S("restart"), cache, 1, 800);
		Check(ctx.Journal.Notes.Any(n => n.Contains("the game closed while it compiled", StringComparison.Ordinal) && n.Contains("no crash report", StringComparison.Ordinal)), "no report since this start: said as a close, an older report is not this one's");
		var report = Path.Combine(dir, "SkyrimDiag_Crash_20261110_120327_711_SkyrimDiagReport.txt");
		File.WriteAllText(report, "the report");
		File.WriteAllText(Path.Combine(dir, "SkyrimDiag_Crash_20261110_120327_711.dmp"), "dump");
		ShaderStep.GameEnded(ctx, S("restart"), cache, 1, 800);
		Check(ctx.Journal.Notes.Any(n => n.Contains("CRASHED", StringComparison.Ordinal) && n.Contains(report, StringComparison.Ordinal)), "a crash is called a crash, with its report");
		Check(Files.ReadSharedText(ctx.Log.TextPath!).Contains(report, StringComparison.Ordinal), "in the run's log too");
		Equal(0, asked.Count, "\"restart\": carried on without asking");

		ShaderStep.GameEnded(ctx, S("ask"), cache, 2, 900);
		Check(asked.Count == 1 && asked[0].Contains("CRASHED", StringComparison.Ordinal) && asked[0].Contains("Press Done", StringComparison.Ordinal), "the default asks, and Done carries on");
		try {
			ShaderStep.GameEnded(ctx, S("stop"), cache, 1, 800);
			Check(false, "\"stop\" went on");
		} catch (StepFailedException e) {
			Check(e.Message.Contains(report, StringComparison.Ordinal), "\"stop\" ends the step naming the report");
		}
		var nobody = StepContext(f, "shaders", a_guide: new FakeGuide((_, ct) => {
			ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
			ct.ThrowIfCancellationRequested();
		}));
		nobody.GameLaunched = ctx.GameLaunched;
		var sw = Stopwatch.StartNew();
		try {
			ShaderStep.GameEnded(nobody, S("ask", 0.005), cache, 1, 800);
			Check(false, "nobody answered and it went on");
		} catch (StepFailedException e) {
			Check(e.Message.Contains("nobody answered", StringComparison.Ordinal) && sw.Elapsed < TimeSpan.FromSeconds(10), "nobody there: the step stops, it never starts the game by itself");
		}
		using var stop = new CancellationTokenSource();
		var stopped = StepContext(f, "shaders", a_guide: new FakeGuide((_, ct) => { stop.Cancel(); ct.ThrowIfCancellationRequested(); }), a_cancel: stop.Token);
		Throws<OperationCanceledException>(() => ShaderStep.GameEnded(stopped, S("ask"), cache, 1, 800), "a Stop while it asks is a Stop");
		try {
			ShaderStep.GameEnded(ctx, new StepSettings(new JsonObject { ["on_crash"] = "restart", ["max_restarts"] = 3 }), cache, 4, 900);
			Check(false, "a fourth crash went on");
		} catch (StepFailedException e) {
			Check(e.Message.Contains("4 times", StringComparison.Ordinal), "more crashes than the step allows end it");
		}
		ctx.Log.Dispose();
		nobody.Log.Dispose();
		stopped.Log.Dispose();
	}

	// A failed step takes out of overwrite only what it made. A path new to overwrite with a date from before the step (a
	// crash logger renaming its own older logs) stays, and a crash report that goes to the backup is named with its new place.
	static void T_AFailedStepTakesOnlyItsOwnFilesOutOfOverwrite()
	{
		var began = new DateTime(2026, 11, 10, 16, 42, 0, DateTimeKind.Utc);
		var before = new Dictionary<string, DateTime> { ["a.ini"] = began.AddHours(-3), ["b.ini"] = began.AddHours(-3) };
		var now = new Dictionary<string, DateTime> {
			["a.ini"] = began.AddHours(-3), ["b.ini"] = began.AddMinutes(5), ["new.dds"] = began.AddMinutes(9), ["previous.2.log"] = began.AddHours(-6), ["copied.nif"] = began.AddDays(-30),
		};
		var (made, older) = Runner.NewInOverwrite(before, now, began, p => p == "copied.nif" ? began.AddMinutes(3) : began.AddDays(-40));
		Check(made.Order().SequenceEqual(["b.ini", "copied.nif", "new.dds"]), "changed, written or created since the step began: " + string.Join(", ", made));
		Check(older.SequenceEqual(["previous.2.log"]), "an older file under a new name is not the step's");

		using var f = new FakeMo2();
		var logs = Path.Combine(f.Overwrite, "SKSE", "Plugins", "Tullius Ctd Logs");
		Directory.CreateDirectory(logs);
		var oldLog = Path.Combine(logs, "Helper.previous.1.log");
		File.WriteAllText(oldLog, "from this morning");
		File.SetCreationTime(oldLog, DateTime.Now.AddHours(-7));
		File.SetLastWriteTime(oldLog, DateTime.Now.AddHours(-7));
		var report = "SkyrimDiag_Crash_20261110_120327_711_SkyrimDiagReport.txt";
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			File.Move(oldLog, Path.Combine(logs, "Helper.previous.2.log"));
			File.WriteAllText(Path.Combine(logs, report), "the crash");
			File.WriteAllText(Path.Combine(f.Overwrite, "half.nif"), "half");
			throw new StepFailedException("the game crashed");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(File.Exists(Path.Combine(logs, "Helper.previous.2.log")), "the older log the step only renamed stays in overwrite");
		Check(!File.Exists(Path.Combine(f.Overwrite, "half.nif")) && !File.Exists(Path.Combine(logs, report)), "what the step made left it");
		var moved = Directory.EnumerateFiles(f.Backups, report, SearchOption.AllDirectories).FirstOrDefault();
		Check(moved != null, "the crash report is in the run's backup");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		var log = File.ReadAllText(Path.Combine(j.Folder, "log.txt"));
		Check(log.Contains("crash report went with them - it is now at " + moved, StringComparison.Ordinal), "and the log says where it went");
		Check(log.Contains("left where they are (not this run's): Helper.previous.2.log", StringComparison.Ordinal), "and names what it left");
	}

	// A step that makes several outputs keeps the finished one on when a later part fails (the first cache's fate hung on
	// the second's)
	static void T_AFinishedPartStaysOnWhenTheStepFailsLater()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.EnsureOutput(ctx, "Part One");
			File.WriteAllText(Path.Combine(f.Mods, "Part One", "made.txt"), "whole");
			ctx.Finished("Part One");
			StepKit.EnsureOutput(ctx, "Part Two");
			throw new StepFailedException("the second part failed");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "the run failed at that step");
		var list = f.Text("modlist.txt");
		Check(list.Contains("+Part One", StringComparison.Ordinal), "the finished part is in the list and ON");
		Check(!list.Contains("Part Two", StringComparison.Ordinal), "the part nothing was built into is taken out");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(j.Built.Contains("Part One"), "on record as built (a put-back after a crash keeps it too)");
		Check(File.Exists(Path.Combine(f.Mods, "Part One", "made.txt")), "its files stay");
	}

	// Stop is seen by every wait of a put-back. Pressed during Root Builder's three-minute wait (a close with no
	// cancellation of its own), it was seen only after Mod Organizer had been started again - and then the put-back met that fresh
	// Mod Organizer and the step read FAILED "Mod Organizer is open".
	static void T_StopIsSeenByAPutBacksWaits()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", new JsonObject()));
		var keep = (Mo2Control.RootBuilderWait, Mo2Control.RootBuilderLook, Mo2Control.StartingGrace, Mo2Control.ReadyQuiet);
		try {
			Mo2Control.RootBuilderWait = TimeSpan.FromSeconds(30);
			Mo2Control.RootBuilderLook = TimeSpan.FromMilliseconds(20);
			using var stop = new CancellationTokenSource();
			var t = Stopwatch.StartNew();
			var waiter = Task.Run(() => Mo2Control.WaitForClear(() => true, CancellationToken.None, stop.Token));
			Thread.Sleep(150);
			Check(!waiter.IsCompleted, "it waits while nobody asked to stop");
			stop.Cancel();
			Check(waiter.Wait(TimeSpan.FromSeconds(5)) && !waiter.Result && t.Elapsed < TimeSpan.FromSeconds(10), "a Stop ends it at once, without a throw (the put-back goes on)");
			Throws<OperationCanceledException>(() => Mo2Control.WaitForClear(() => true, stop.Token, stop.Token), "the caller's own token still throws");

			// a put-back's close (no token of its own) under a pressed Stop: Root Builder is not waited for
			// (its log is let go however this block ends: left open after a check that failed, it held the test's folder)
			var ctx = StepContext(f, "landscapefixer");
			using var ctxLog = ctx.Log;
			var looks = 0;
			var control = new Mo2Control(ctx.Mo2, "Test", ctx.Log) { RunningProbe = () => ++looks < 2, ForeignProbe = () => [], RootBuilderProbe = () => true, StopAsked = stop.Token };
			t.Restart();
			control.Close();
			Check(t.Elapsed < TimeSpan.FromSeconds(10), $"closed in {t.Elapsed.TotalSeconds:0.0} s, not after Root Builder's wait");
			Check(Files.ReadSharedText(ctx.Log.TextPath!).Contains("Stop: not waiting for Root Builder", StringComparison.Ordinal), "and it says so");

			// a Stop already asked for: no switch is written and nothing starts
			var before = f.Text("modlist.txt");
			var cancelled = StepContext(f, "landscapefixer", a_cancel: stop.Token);
			using var cancelledLog = cancelled.Log;
			Throws<OperationCanceledException>(() => new Toggle(cancelled, [], ["Base Mod"], "a test", stop.Token).Dispose(), "a switch under a pressed Stop");
			Equal(before, f.Text("modlist.txt"), "the mod list was not touched");
			var started = 0;
			var never = new Mo2Control(ctx.Mo2, "Test", RunLog.Null) { RunningProbe = () => false, Launch = (_, _) => ++started };
			Throws<OperationCanceledException>(() => never.Start(stop.Token), "Mod Organizer asked to start under a pressed Stop");
			Equal(0, started, "it was not started");

			// a Mod Organizer the run started a moment ago counts as running before Windows lists it by name
			Mo2Control.StartingGrace = TimeSpan.FromSeconds(2);
			Mo2Control.ReadyQuiet = TimeSpan.Zero;
			var fresh = new Mo2Control(ctx.Mo2, "Test", RunLog.Null) { Launch = (_, _) => Environment.ProcessId, ReadyProbe = () => new(true, true, TimeSpan.Zero, null) };
			fresh.Start(CancellationToken.None);
			Check(fresh.Running, "running, though no ModOrganizer.exe of this instance is listed yet");
			Thread.Sleep(2200);
			Check(!fresh.Running, "only for its first seconds");

			// the put-back meets a Mod Organizer that came up after its close: it closes it and puts the mods back, no failure
			var open = 0;
			var mo2 = ctx.Mo2;
			var late = new RunContext { Setup = ctx.Setup, Mo2 = mo2, Profile = new Profile(mo2, "Test", () => open-- > 0), Control = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => false },
				Log = ctx.Log, Journal = ctx.Journal, Cancel = CancellationToken.None, BackupFolder = ctx.BackupFolder };
			var toggle = new Toggle(late, [], ["Base Mod"], "a test");
			Check(f.Text("modlist.txt").Contains("-Base Mod", StringComparison.Ordinal), "switched off for the step");
			open = 1;
			toggle.Dispose();
			Equal(before, f.Text("modlist.txt"), "put back all the same");

			// ... and that Mod Organizer saved a change of the player's before it was closed again: looked at again, and kept
			var comesUp = false;
			var listFile = Path.Combine(f.ProfileDir, "modlist.txt");
			var watched = new Profile(mo2, "Test", () => {
				if (!comesUp) {
					return false;
				}
				comesUp = false;
				File.WriteAllText(listFile, File.ReadAllText(listFile).Replace("+Replaced", "-Replaced", StringComparison.Ordinal));
				return true;
			});
			watched.WatchForOthers();
			var changed = new RunContext { Setup = ctx.Setup, Mo2 = mo2, Profile = watched, Control = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => false },
				Log = ctx.Log, Journal = ctx.Journal, Cancel = CancellationToken.None, BackupFolder = ctx.BackupFolder };
			var countedBefore = ctx.Journal.FilesChanged;
			var second = new Toggle(changed, [], ["Base Mod"], "a test");
			comesUp = true;
			second.Dispose();
			Check(f.Text("modlist.txt").Contains("-Replaced", StringComparison.Ordinal) && f.Text("modlist.txt").Contains("+Base Mod", StringComparison.Ordinal),
				"what the player changed in the Mod Organizer that came up is kept, and the step's own switch is back: " + f.Text("modlist.txt").Replace("\r\n", " "));
			// the step's own switch is taken back and counts for nothing; the mod the player switched stands otherwise than
			// before the step, so the game finds other files than its list names
			Equal(countedBefore + 1, ctx.Journal.FilesChanged, "what the player switched during the step counts as a change to the game's files");
			// the same switch, still standing at the next step, was counted: it is not counted at every step again
			var third = new Toggle(changed, [], ["Base Mod"], "a test");
			third.Dispose();
			Equal(countedBefore + 1, ctx.Journal.FilesChanged, "a switch of theirs that was counted is not counted a second time at the next step");
			// A step that starts the game with a new list of its files and switches no mod itself; the player switches one
			// while it runs. The finished step calls that list current (the count goes to nothing) - and the player's switch
			// is counted after that, not wiped with it: the list is older than what they switched
			changed.FreshList = true;
			var fourth = new Toggle(changed, [], [], "a game step");
			File.WriteAllText(listFile, File.ReadAllText(listFile).Replace("+Master Mod", "-Master Mod", StringComparison.Ordinal));
			fourth.Dispose();
			Check(f.Text("modlist.txt").Contains("-Master Mod", StringComparison.Ordinal), "(what they switched is kept)");
			Crdw.StepEnded(changed, true, false);
			Equal(1, ctx.Journal.FilesChanged, "a mod the player switched after the game made its list leaves that list old: counted once, after the step called it current");
			// The player takes that switch back, and no step's own switching ends before the next game start: the look
			// right before the start knows it as taken back (the game makes its list with the mod on). The same switch made
			// again after that start counts again - against the state the list was made from
			File.WriteAllText(listFile, File.ReadAllText(listFile).Replace("-Master Mod", "+Master Mod", StringComparison.Ordinal));
			Crdw.Apply(changed);
			Equal(2, ctx.Journal.FilesChanged, "a switch taken back before a game start is seen right before that start");
			changed.FreshList = true;
			Crdw.StepEnded(changed, true, false);
			Equal(0, ctx.Journal.FilesChanged, "(the game made its list with that mod on: the list is current)");
			File.WriteAllText(listFile, File.ReadAllText(listFile).Replace("+Master Mod", "-Master Mod", StringComparison.Ordinal));
			var fifth = new Toggle(changed, [], [], "a later step");
			fifth.Dispose();
			Equal(1, ctx.Journal.FilesChanged, "made again after that start, the switch counts again: the game's list was made without it");
		} finally {
			(Mo2Control.RootBuilderWait, Mo2Control.RootBuilderLook, Mo2Control.StartingGrace, Mo2Control.ReadyQuiet) = keep;
		}
	}

	// ------------------------------------------------------------------ the engine's edge cases
	// A file another program holds for a moment (an antivirus, the indexer) is tried again, a write over a read-only file keeps
	// it read-only, a write that fails leaves no temp file, and text that is not UTF-8 is read in the PC's own code page
	static void T_AFileHeldForAMomentIsTriedAgain()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "held " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		try {
			var path = Path.Combine(dir, "record.json");
			File.WriteAllText(path, "old");
			using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
				var letGo = Task.Run(() => {
					Thread.Sleep(300);
					held.Dispose();
				});
				Files.WriteAtomic(path, "new");
				letGo.Wait();
			}
			Equal("new", File.ReadAllText(path), "written once the other program let go");
			var moved = Path.Combine(dir, "moved.json");
			using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
				var letGo = Task.Run(() => {
					Thread.Sleep(300);
					held.Dispose();
				});
				Files.Move(path, moved);
				letGo.Wait();
			}
			Check(File.Exists(moved) && !File.Exists(path), "moved once it was free");
			using (new FileStream(moved, FileMode.Open, FileAccess.Read, FileShare.None)) {
				// Windows refuses a file held open as a sharing violation or as "access denied", by how it is asked
				var refused = false;
				try {
					Files.WriteAtomic(moved, "never");
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					refused = true;
				}
				Check(refused, "a file that stays held fails in the end");
			}
			Equal("new", File.ReadAllText(moved), "and is as it was");
			Check(Directory.GetFiles(dir).Length == 1, "no temp file is left beside it: " + string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));
			File.SetAttributes(moved, FileAttributes.ReadOnly);
			Files.WriteAtomic(moved, "again");
			Check(File.ReadAllText(moved) == "again" && File.GetAttributes(moved).HasFlag(FileAttributes.ReadOnly), "a read-only file is written and stays read-only");
			File.SetAttributes(moved, FileAttributes.Normal);
			// "caf" and an e with an accent as a western PC writes it: not UTF-8
			byte[] ansi = [0x63, 0x61, 0x66, 0xE9];
			var text = Files.DecodeText(ansi, out var how);
			Check(text.Length == 4 && !text.Contains('�', StringComparison.Ordinal), "read in the PC's own code page, no letter lost: " + text);
			Check(Files.EncodeText(text, how).SequenceEqual(ansi), "and written back as the same bytes");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// a log line the disk will not take never throws (lines stand in front of the work on every put-back path) and is owed
	static void T_ALogLineTheDiskRefusesIsOwedNeverThrown()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "log " + Guid.NewGuid().ToString("N")[..8]);
		try {
			var log = new RunLog(dir);
			log.Say("written");
			log.Dispose();
			log.Say("after the file was let go");
			log.Simple("and a simple one");
			Equal(2, log.Owed, "both lines are owed, neither threw");
			Check(File.ReadAllText(Path.Combine(dir, "log.txt")).Contains("written", StringComparison.Ordinal), "what was written before stays");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// the newest run's record must read before "no run was interrupted" is believed; a check that cannot look refuses in words
	static void T_ARunIsRefusedInWordsWhenItCannotBeSureOrCannotLook()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		f.WriteSetup(("landscapefixer", []));
		var before = f.Text("modlist.txt");
		Preflight.GameCheck = () => throw new IOException("the drive is not ready");
		try {
			var blind = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(blind.State == RunState.Failed && blind.Verdict.Contains("could not finish", StringComparison.Ordinal) && blind.Verdict.Contains("the drive is not ready", StringComparison.Ordinal),
				"a check that cannot look refuses the run in words: " + blind.Verdict);
		} finally {
			Preflight.GameCheck = () => [];
		}
		var bad = Path.Combine(Runner.RunsDir(f.Data), "9999-12-31 damaged");
		Directory.CreateDirectory(bad);
		File.WriteAllText(Path.Combine(bad, "journal.json"), "{ \"Id\": ");
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.State == RunState.Failed && r.Verdict.Contains("is damaged", StringComparison.Ordinal), "refused over a newest record that does not read: " + r.Verdict);
		Equal(before, f.Text("modlist.txt"), "nothing changed");
	}

	// "stop after this step" pressed during the last tool holds before the final save too (it once started the game all the same)
	static void T_StopAfterHoldsBeforeTheSaveToPlay()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			Commands.Send(ctx.Journal.Folder, "stop after");
			Thread.Sleep(1200);
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		var played = false;
		StepRegistry.Replace(new FakeStep("playsave", (ctx, s) => played = true));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("playsave", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Stopped, r.State, "stopped: " + r.Verdict);
		Check(!played, "the game was not started for the final save");
		Check(r.Verdict.Contains("carry on from Final save", StringComparison.Ordinal), "and it says where to carry on: " + r.Verdict);
		Check(ProfileOf(f).IsOn("Output A"), "the finished output on");
	}

	// the setup's after-run commands run once whatever the run did: after one that failed, and after one that finished
	static void T_AfterRunCommandsRunOnceWhateverHappens()
	{
		using var f = new FakeMo2();
		var marker = Path.Combine(f.Root, "after ran.txt");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new StepFailedException("the tool crashed")));
		var s = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		s.Root["afterRun"] = new JsonArray($"echo ran>>\"{marker}\"");
		s.Save();
		Equal(RunState.Failed, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run that failed");
		Equal(1, File.ReadAllLines(marker).Length, "after a failed run: once");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		Thread.Sleep(1100);
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "the run that finished: " + r.Verdict);
		Equal(2, File.ReadAllLines(marker).Length, "after a finished run: once more, not twice");
		var j = Journal.Load(r.Folder!);
		Check(!j.FinishingUp && j.Doing == "" && j.Pid == 0, "and the record is let go: no chore, no owner");
	}

	// A Stop pressed while a finished run is at its after-run commands never skips one (they give back what the before-run
	// commands took): the one running is waited for a short while more and then ended, the next one still runs, the run stays
	// finished
	static void T_AStopNeverSkipsAnAfterRunCommand()
	{
		using var f = new FakeMo2();
		var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
		var marker = Path.Combine(f.Root, "after ran.txt");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		var s = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		s.Root["afterRun"] = new JsonArray($"\"{ping}\" -n 40 127.0.0.1", $"echo second>>\"{marker}\"");
		s.Root["zipBackups"] = false;
		s.Save();
		var keep = Runner.HookHurry;
		// the hurry each command gets once Stop is pressed: long enough for the second command (an echo) to start and end on a
		// PC busy with other work - at 2 s it was itself cut off there - and well short of the first one's 39 s
		Runner.HookHurry = TimeSpan.FromSeconds(8);
		try {
			var runs = Runner.RunsDir(f.Data);
			var stopper = Task.Run(() => {
				for (var i = 0; i < 600; ++i) {
					var d = Directory.Exists(runs) ? Directory.GetDirectories(runs).FirstOrDefault() : null;
					try {
						if (d != null && File.Exists(Path.Combine(d, "journal.json")) && Journal.Load(d) is { FinishingUp: true, Doing: "the setup's command after the run" }) {
							Thread.Sleep(500);
							Commands.Send(d, "stop");
							return true;
						}
					} catch (Exception e) when (e is IOException or System.Text.Json.JsonException) {
					}
					Thread.Sleep(50);
				}
				return false;
			});
			var sw = Stopwatch.StartNew();
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(stopper.Result, "(the Stop was sent while the first command ran)");
			Equal(RunState.Finished, r.State, "the run stays finished: " + r.Verdict);
			Check(sw.Elapsed < TimeSpan.FromSeconds(25), $"the command was not waited for to its own end (39 s): {sw.Elapsed.TotalSeconds:0.0} s");
			Check(File.Exists(marker) && File.ReadAllLines(marker).Length == 1, "the command after it still ran, once");
			var j = Journal.Load(r.Folder!);
			Check(j.Notes.Any(n => n.Contains("of Stop", StringComparison.Ordinal)), "the one that was ended is in the run's notes: " + string.Join("; ", j.Notes));
		} finally {
			Runner.HookHurry = keep;
		}
	}

	// A setup's own command is ended by Stop at once and by its limit, and one that leaves a program running behind it (which
	// holds the command's output open) never keeps the run waiting for that program
	static void T_ACommandIsEndedByStopAndByItsLimit()
	{
		var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
		using (var f = new FakeMo2()) {
			StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
			var s = f.WriteSetup(("landscapefixer", []));
			s.Root["beforeRun"] = new JsonArray($"\"{ping}\" -n 40 127.0.0.1");
			s.Save();
			var before = f.Text("modlist.txt");
			var runs = Runner.RunsDir(f.Data);
			var stopper = Task.Run(() => {
				for (var i = 0; i < 400; ++i) {
					var d = Directory.Exists(runs) ? Directory.GetDirectories(runs).FirstOrDefault() : null;
					if (d != null && File.Exists(Path.Combine(d, "log.txt"))) {
						Thread.Sleep(800);
						Commands.Send(d, "stop");
						return;
					}
					Thread.Sleep(50);
				}
			});
			var sw = Stopwatch.StartNew();
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			stopper.Wait();
			Check(sw.Elapsed < TimeSpan.FromSeconds(20), $"Stop ended the command after {sw.Elapsed.TotalSeconds:0.0} s, not after its 40");
			Check(r.State == RunState.Stopped && r.Verdict.Contains("Stop was pressed", StringComparison.Ordinal), "the run reads as stopped: " + r.Verdict);
			Equal(before, f.Text("modlist.txt"), "nothing changed");
		}
		using (var f = new FakeMo2()) {
			StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
			var s = f.WriteSetup(("landscapefixer", []));
			s.Root["beforeRun"] = new JsonArray($"\"{ping}\" -n 40 127.0.0.1");
			s.Save();
			var keep = Runner.HookLimit;
			Runner.HookLimit = TimeSpan.FromSeconds(2);
			try {
				var sw = Stopwatch.StartNew();
				var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
				Check(r.State == RunState.Failed && r.Verdict.Contains("did not end within 2 seconds", StringComparison.Ordinal), "the limit ends it, named: " + r.Verdict);
				Check(sw.Elapsed < TimeSpan.FromSeconds(20), $"after {sw.Elapsed.TotalSeconds:0.0} s");
			} finally {
				Runner.HookLimit = keep;
			}
		}
		using (var f = new FakeMo2()) {
			// (ping under a name of this test's own, so the copy left behind is ended by that name once the test is over)
			var leftDir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "left behind " + Guid.NewGuid().ToString("N")[..8]);
			var leftName = "left behind " + Guid.NewGuid().ToString("N")[..8] + ".exe";
			// (inside the try: a copy that fails half way still has its folder taken away)
			try {
				Directory.CreateDirectory(leftDir);
				File.Copy(ping, Path.Combine(leftDir, leftName));
				var sw = Stopwatch.StartNew();
				var stepAt = TimeSpan.Zero;
				StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => stepAt = sw.Elapsed));
				var s = f.WriteSetup(("landscapefixer", []));
				s.Root["beforeRun"] = new JsonArray($"start \"\" /b \"{Path.Combine(leftDir, leftName)}\" -n 30 127.0.0.1");
				s.Save();
				var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
				Equal(RunState.Finished, r.State, r.Verdict);
				Check(stepAt > TimeSpan.Zero && stepAt < TimeSpan.FromSeconds(15), $"the first step began after {stepAt.TotalSeconds:0.0} s, not once the program left behind had ended (29 s)");
			} finally {
				foreach (var pid in Procs.Pids(leftName)) {
					Procs.KillTree(pid);
				}
				try {
					if (Directory.Exists(leftDir)) {
						Directory.Delete(leftDir, true);
					}
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				}
			}
		}
	}

	// Packing the old outputs can be stopped (the folder whole, no half-made zip), is not begun without the room for it, says
	// what it is doing, and keeps empty folders and the files' times
	static void T_PackingCanBeStoppedAndSaysWhatItDoes()
	{
		using var f = new FakeMo2();
		var dir = Path.Combine(f.Root, "pack stop");
		var mod = Path.Combine(dir, RunUndo.OutputsFolder, "Mod");
		Directory.CreateDirectory(Path.Combine(mod, "Empty"));
		var file = Path.Combine(mod, "a.txt");
		File.WriteAllText(file, "a");
		var written = new DateTime(2020, 5, 5, 10, 20, 30, DateTimeKind.Local);
		File.SetLastWriteTime(file, written);
		var zip = Path.Combine(dir, RunUndo.OutputsZip);
		using (var stop = new CancellationTokenSource()) {
			stop.Cancel();
			Throws<OperationCanceledException>(() => RunUndo.Pack(dir, RunLog.Null, null, stop.Token), "a Stop ends the packing");
		}
		Check(!File.Exists(zip) && !File.Exists(zip + ".part") && File.ReadAllText(file) == "a", "stopped: the folder whole, no zip and no half-made one");
		var room = InGame.FreeBytes;
		InGame.FreeBytes = _ => 0;
		try {
			RunUndo.Pack(dir, RunLog.Null);
			Check(!File.Exists(zip) && File.Exists(file), "no room for the zip beside the folder: not begun");
		} finally {
			InGame.FreeBytes = room;
		}
		var said = new List<string>();
		RunUndo.Pack(dir, RunLog.Null, said.Add);
		Check(said.Count > 0 && said[0].Contains("packing the old outputs", StringComparison.Ordinal), "it says what it is doing: " + string.Join(" | ", said));
		Check(File.Exists(zip) && !Directory.Exists(Path.Combine(dir, RunUndo.OutputsFolder)), "packed, the folder gone");
		RunUndo.Unpack(dir);
		Check(Directory.Exists(Path.Combine(mod, "Empty")), "an empty folder comes back");
		Equal(written, File.GetLastWriteTime(file), "a file comes back with its time");
	}

	// A Stop in the seconds a step has just ended (Mod Organizer is being closed to tick its output): the step is on record as
	// done - never left "running" for good - the next step never starts, and the put-back ticks what was built
	static void T_AStopAsAStepEndsLeavesNoStepRunning()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			Commands.Send(ctx.Journal.Folder, "stop");
			for (var i = 0; i < 200 && !ctx.Cancel.IsCancellationRequested; ++i) {
				Thread.Sleep(50);
			}
			// a Mod Organizer is up at the moment the step ends (the player opened one): gone again at the next look
			var looks = 0;
			ctx.Control.RunningProbe = () => looks++ < 1;
		}));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => throw new InvalidOperationException("must not run")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Stopped, r.State, "stopped: " + r.Verdict);
		var j = Journal.Load(r.Folder!);
		Equal(StepState.Done, j.Step("landscapefixer")!.State, "the step that finished is on record as done");
		Equal(StepState.Skipped, j.Step("synthesis")!.State, "the Stop was met while its output was being ticked: the next step never began");
		Check(j.Steps.All(s => s.State != StepState.Running), "no step is left running");
		Check(ProfileOf(f).IsOn("Output A") && File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")), "the put-back ticked what it built");
	}

	// the put-back after a Stop still ticks what was built when a Mod Organizer came up in between (its close there is never
	// ended by the Stop that asked for the put-back)
	static void T_ThePutBackTicksWhatWasBuiltUnderAStop()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		using var stop = new CancellationTokenSource();
		stop.Cancel();
		var ctx = StepContext(f, "landscapefixer", a_cancel: stop.Token);
		try {
			ctx.Profile.SetMods([], ["Needs B", "Output B", "Output A"]);
			var snap = ctx.Profile.Snapshot();
			ctx.Journal.Built.Add("Output A");
			// closed at the put-back's first look, up when the built output is to be ticked, closed once asked to
			var looks = 0;
			ctx.Control.RunningProbe = () => ++looks == 2;
			Runner.PutBack(ctx, snap);
			Check(ProfileOf(f).IsOn("Output A"), "the built output is on");
			Check(looks >= 2, "(a Mod Organizer was met on the way)");
		} finally {
			ctx.Log.Dispose();
		}
	}

	// An old output that is no longer in the run's backup when its step fails (the backup cleared by hand): said, the mod kept
	// off and never ticked holding nothing, and the run is not held up for a put-back that can never happen
	static void T_AnOldOutputGoneFromTheBackupIsSaidAndKeptOff()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "landscapefixer", "Output A");
			Directory.Delete(ctx.Journal.Parked.Last(p => p.Mod == "Output A").To, true);
			throw new StepFailedException("the tool crashed");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "failed");
		Check(r.Verdict.Contains("except Output A", StringComparison.Ordinal) && r.Verdict.Contains("no longer in the backup", StringComparison.Ordinal), "the verdict names it: " + r.Verdict);
		Check(!ProfileOf(f).IsOn("Output A"), "the mod is off, never on holding nothing");
		var j = Journal.Load(r.Folder!);
		Check(j.Parked.Single().Lost && !j.Parked.Single().PutBack, "on record as lost, not as put back");
		Check(j.Notes.Any(n => n.Contains("could not be put back", StringComparison.Ordinal)), "and in the run's notes");
		Check(Journal.Interrupted(Runner.RunsDir(f.Data)) == null, "nothing waits for a put-back that cannot happen");
	}

	// Restore ends a cut-off step when its run died - the record's last write before the put-back - not when Restore was pressed;
	// a Mod Organizer that is gone is said in words by Restore and by Undo, never thrown
	static void T_RestoreKnowsWhenTheRunDiedAndSaysWhatIsMissing()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "landscapefixer", "Output A");
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		var j = Journal.Interrupted(Runner.RunsDir(f.Data))!;
		var died = DateTime.Now.AddHours(-3);
		File.SetLastWriteTime(j.File, died);
		File.Move(f.Exe, f.Exe + ".gone");
		var missing = Runner.Restore(Journal.Load(j.Folder));
		Check(missing.State == RunState.Failed && missing.Verdict.Contains("was not found", StringComparison.Ordinal), "a Mod Organizer that is gone is said: " + missing.Verdict);
		File.Move(f.Exe + ".gone", f.Exe);
		File.SetLastWriteTime(j.File, died);
		Equal(RunState.Restored, Runner.Restore(Journal.Load(j.Folder)).State, "restored once it is back");
		var ended = Journal.Load(j.Folder).Steps.Single().Ended!.Value;
		Check(Math.Abs((ended - died).TotalMinutes) < 1, $"the cut-off step ended when the run died ({died:HH:mm}), not when Restore was pressed: {ended:HH:mm}");

		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		Thread.Sleep(1100);
		var done = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, done.State, done.Verdict);
		File.Move(f.Exe, f.Exe + ".gone");
		try {
			var undo = Runner.Undo(Journal.Load(done.Folder!));
			Check(undo.Verdict.Contains("nothing was undone", StringComparison.Ordinal) && undo.Verdict.Contains("was not found", StringComparison.Ordinal), "Undo says it too: " + undo.Verdict);
		} finally {
			File.Move(f.Exe + ".gone", f.Exe);
		}
	}

	// one file of a failed step that will not move out of overwrite (still held by its tool) never keeps the others there
	static void T_OneHeldFileNeverKeepsTheRestInOverwrite()
	{
		using var f = new FakeMo2();
		FileStream? held = null;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			File.WriteAllText(Path.Combine(f.Overwrite, "a held.log"), "the tool's log");
			File.WriteAllText(Path.Combine(f.Overwrite, "half.nif"), "half");
			held = new FileStream(Path.Combine(f.Overwrite, "a held.log"), FileMode.Open, FileAccess.Read, FileShare.None);
			throw new StepFailedException("the tool crashed");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(!File.Exists(Path.Combine(f.Overwrite, "half.nif")), "the file that could move left overwrite");
			Check(File.Exists(Path.Combine(f.Overwrite, "a held.log")), "(the held one is still there)");
			var j = Journal.Load(r.Folder!);
			Check(j.Notes.Any(n => n.Contains("could not be moved out", StringComparison.Ordinal) && n.Contains("a held.log", StringComparison.Ordinal)), "and the run's notes name it: " + string.Join("; ", j.Notes));
		} finally {
			held?.Dispose();
		}
	}

	// a run that has finished and is at its last chores (the setup's commands, packing the old outputs) is still the run going
	// to the window while its engine lives - Stop and the close guard reach it - and nothing to put back
	static void T_ARunAtItsLastChoresIsStillTheRunGoing()
	{
		var j = new Journal { State = RunState.Finished, FinishingUp = true, Pid = Environment.ProcessId, PidStarted = StartedUtc() };
		Check(j.Live && !j.Unfinished, "live, and nothing to put back");
		Check(ToolRunner.App.Engine.Active([j]) == j, "the window follows it as the run going");
		j.FinishingUp = false;
		Check(ToolRunner.App.Engine.Active([j]) == null, "not once its chores are done");
		j.FinishingUp = true;
		j.Pid = 999999;
		Check(ToolRunner.App.Engine.Active([j]) == null && ToolRunner.App.Engine.Interrupted([j]) == null, "an engine that died at its chores left a finished run: neither going nor to put back");
	}

	[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
	static extern nint CreateWindowExW(uint a_ex, string a_class, string a_title, uint a_style, int a_x, int a_y, int a_w, int a_h, nint a_parent, nint a_menu, nint a_instance, nint a_param);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
	static extern bool DestroyWindow(nint a_hwnd);

	// a window that takes messages only: never on anyone's screen
	static nint MessageWindow(string a_text) => CreateWindowExW(0, "STATIC", a_text, 0, 0, 0, 0, 0, -3, 0, 0, 0);

	// Mod Organizer is started through Windows' management service, so it is that service's child and none of the run's (a
	// child of a job-limited process makes it fail to start the script extender). The start itself, with a command that
	// shows nothing and ends by itself: where it runs, under whom, and what a program that is not there comes to
	static void T_ADetachedStartIsNoChildOfTheProgram()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "detached " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		var pid = 0;
		try {
			var file = Path.Combine(dir, "ran in.txt");
			var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
			// (it waits a minute: alive for as long as it is looked at, whatever this test's thread is given, and ended below)
			pid = Mo2Control.StartDetached($"\"{cmd}\" /c cd > \"{file}\" & ping -n 60 127.0.0.1 > nul", Environment.SystemDirectory, 0);
			Check(pid > 0 && pid != Environment.ProcessId, $"Windows gave the process's number: {pid}");
			var parent = Procs.Snapshot().FirstOrDefault(r => r.Pid == pid).Parent;
			Check(parent != 0 && parent != Environment.ProcessId, $"it is no child of this process (its parent: {parent})");
			using (var host = Process.GetProcessById(parent)) {
				Check(host.ProcessName.StartsWith("WmiPrvSE", StringComparison.OrdinalIgnoreCase), "its parent is Windows' own management host: " + host.ProcessName);
			}
			var sw = Stopwatch.StartNew();
			while (sw.Elapsed < TimeSpan.FromSeconds(10) && !(File.Exists(file) && new FileInfo(file).Length > 0)) {
				Thread.Sleep(100);
			}
			Check(File.Exists(file) && string.Equals(File.ReadAllText(file).Trim(), Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase),
				"it ran in the folder it was given, with its command line whole");
			var gone = Path.Combine(dir, "no such folder", "none.exe");
			try {
				Mo2Control.StartDetached($"\"{gone}\"", dir, 0);
				Check(false, "a program that is not there is not started");
			} catch (InvalidOperationException e) {
				Check(e.Message.StartsWith("Windows could not start \"" + gone, StringComparison.Ordinal) && e.Message.Contains("(code ", StringComparison.Ordinal),
					"a program that is not there is said, with Windows' own code: " + e.Message);
			}
		} finally {
			// the command this test started, ended by its number and start time (and its ping with it), then its folder
			if (pid > 0 && Procs.StartTime(pid) is { } at) {
				Procs.KillTree(pid, at);
			}
			Directory.Delete(dir, true);
		}
	}

	// A tool whose window thread hangs never holds the step that reads it: a message to it has a time limit, no answer reads as
	// no text, and the step's own clock goes on (a plain send once sat there for as long as the tool hung)
	static void T_AHungWindowNeverHoldsAStep()
	{
		var mine = MessageWindow("its log says done");
		Check(mine != 0, "(a window to ask)");
		nint hung = 0;
		using var made = new ManualResetEventSlim();
		using var letGo = new ManualResetEventSlim();
		// its own thread makes it and then never looks at a message again
		var owner = new Thread(() => {
			hung = MessageWindow("never read");
			made.Set();
			letGo.Wait();
			DestroyWindow(hung);
		}) { IsBackground = true };
		var keep = Native.SendLimitMs;
		Native.SendLimitMs = 300;
		try {
			owner.Start();
			made.Wait();
			Check(hung != 0, "(a hung window to ask)");
			var buf = Array.Empty<char>();
			Equal((17, "its log says done"), Native.ControlTail(mine, 4000, ref buf), "a window that answers is read whole");
			Equal((17, "done"), Native.ControlTail(mine, 4, ref buf), "or by its end alone");
			Check(buf.Length >= 18, "into the buffer its caller keeps");
			var sw = Stopwatch.StartNew();
			Check(!Native.TrySend(hung, Native.WM_GETTEXTLENGTH, 0, 0, out _), "a hung window gives no answer");
			Equal((0, ""), Native.ControlTail(hung, 4000, ref buf), "and reads as no text");
			Check(sw.Elapsed < TimeSpan.FromSeconds(5), $"within the limit, not for as long as it hangs: {sw.Elapsed.TotalSeconds:0.0} s");
		} finally {
			Native.SendLimitMs = keep;
			letGo.Set();
			owner.Join(5000);
			DestroyWindow(mine);
		}
	}

	// A tool that closed (or crashed) before it showed it was done is given a short while and then said - never waited for to
	// the end of its hour; the tool clock counts the time the PC was awake
	static void T_AToolThatClosedIsNotWaitedForToItsLimit()
	{
		using var f = new FakeMo2();
		var keep = Waits.GoneGrace;
		Waits.GoneGrace = TimeSpan.FromSeconds(2);
		try {
			StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
				using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
				p.WaitForExit(10000);
				Waits.Tool(ctx, "landscapefixer", p, TimeSpan.FromMinutes(10), () => Waits.Poll.Wait);
			}));
			f.WriteSetup(("landscapefixer", []));
			var sw = Stopwatch.StartNew();
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(r.State == RunState.Failed && r.Verdict.Contains("closed before it finished", StringComparison.Ordinal), "said as closed: " + r.Verdict);
			Check(sw.Elapsed < TimeSpan.FromSeconds(60), $"after {sw.Elapsed.TotalSeconds:0.0} s, not at its ten-minute limit");
		} finally {
			Waits.GoneGrace = keep;
		}
		var awake = AwakeClock.StartNew();
		Thread.Sleep(300);
		Check(awake.Elapsed > TimeSpan.FromMilliseconds(200) && awake.Elapsed < TimeSpan.FromSeconds(10), "the awake clock runs while the PC is awake: " + awake.Elapsed);
	}

	// A tool that has finished and is slow to close never fails its step; DynDOLOD's plugins are waited for until they are
	// saved and quiet before it may be closed
	static void T_AFinishedToolSlowToCloseNeverFailsItsStep()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 40 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
			try {
				StepKit.LetClose(ctx, "landscapefixer", p, TimeSpan.FromSeconds(3), "The tool");
			} finally {
				p.Kill();
			}
		}));
		f.WriteSetup(("landscapefixer", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "the step that had finished stays finished: " + r.Verdict);
		Check(File.ReadAllText(Path.Combine(r.Folder!, "log.txt")).Contains("did not close by itself", StringComparison.Ordinal), "and the log says the tool was closed");

		var (quiet, limit) = (DynDolodStep.SaveQuiet, DynDolodStep.SaveLimit);
		DynDolodStep.SaveQuiet = TimeSpan.FromSeconds(2);
		DynDolodStep.SaveLimit = TimeSpan.FromSeconds(4);
		var ctx = StepContext(f, "landscapefixer");
		try {
			var outDir = Path.Combine(f.Root, "DynDOLOD_Output");
			Directory.CreateDirectory(outDir);
			var since = DateTime.UtcNow.AddSeconds(-1);
			Throws<StepFailedException>(() => DynDolodStep.WaitSaved(ctx, outDir, since), "plugins that never come");
			var lastWrite = TimeSpan.Zero;
			var sw = Stopwatch.StartNew();
			var saving = Task.Run(() => {
				Thread.Sleep(300);
				File.WriteAllText(Path.Combine(outDir, "DynDOLOD.esm"), "esm");
				Thread.Sleep(500);
				File.WriteAllText(Path.Combine(outDir, "DynDOLOD.esp"), "esp");
				Thread.Sleep(1200);
				File.WriteAllText(Path.Combine(outDir, "Occlusion.esp"), "occlusion");
				lastWrite = sw.Elapsed;
			});
			DynDolodStep.SaveLimit = TimeSpan.FromSeconds(30);
			DynDolodStep.WaitSaved(ctx, outDir, since);
			var saved = sw.Elapsed;
			saving.Wait();
			Check(File.Exists(Path.Combine(outDir, "Occlusion.esp")), "the last plugin was waited for");
			Check(saved - lastWrite >= TimeSpan.FromSeconds(1.5), $"and they stood unchanged first: saved at {saved.TotalSeconds:0.0} s, last write at {lastWrite.TotalSeconds:0.0} s");
		} finally {
			(DynDolodStep.SaveQuiet, DynDolodStep.SaveLimit) = (quiet, limit);
			ctx.Log.Dispose();
		}
	}

	// a command-line tool is one of the run's own processes (Stop's sweep, the lower priority), and its last line is read
	// before its output is judged (a finished tool's "Done" line was once missed in the moment it ended)
	static void T_ACommandLineToolIsTheRunsOwnAndItsLastLineIsRead()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", []));
		var ctx = StepContext(f, "landscapefixer");
		try {
			var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
			for (var i = 0; i < 3; ++i) {
				var outFile = Path.Combine(f.Root, $"tool output {i}.txt");
				var (code, text) = ToolRunner.Steps.Cli.Run(ctx, "landscapefixer", cmd, ["/c", "echo working& echo Done in 2.3 min"], TimeSpan.FromMinutes(1), outFile);
				Equal(0, code, "its exit code");
				Check(text.Contains("Done in 2.3 min", StringComparison.Ordinal), $"round {i}: its last line is in what it said: {text}");
				Check(File.ReadAllText(outFile).Contains("working", StringComparison.Ordinal), "and its output is kept in the run's folder");
			}
			Equal(3, ctx.Control.Started.Count, "each start is on record as the run's own");
		} finally {
			ctx.Log.Dispose();
		}
	}

	// Mod Organizer keeps plugins.txt in the PC's own code page and loadorder.txt as UTF-8: a plugin whose name has an accent is
	// read by its real name and keeps its line byte for byte when the file is written again (written as UTF-8 its line named no
	// file any more, and Mod Organizer listed the plugin as new: unticked, at the end of the load order)
	static void T_PluginNamesWithAnAccentKeepTheirLines()
	{
		using var f = new FakeMo2();
		var ansi = Files.AnsiEncoding;
		const string Name = "Übersetzung - Größe.esp";
		if (ansi.GetString(ansi.GetBytes(Name)) != Name) {
			Console.WriteLine("      (skipped: this PC's own code page cannot write the name)");
			return;
		}
		var pluginsTxt = Path.Combine(f.ProfileDir, "plugins.txt");
		var loadOrder = Path.Combine(f.ProfileDir, "loadorder.txt");
		File.WriteAllBytes(pluginsTxt, [.. File.ReadAllBytes(pluginsTxt), .. ansi.GetBytes("*" + Name + "\r\n")]);
		File.WriteAllBytes(loadOrder, [.. File.ReadAllBytes(loadOrder), .. new UTF8Encoding(false).GetBytes(Name + "\r\n")]);
		var before = File.ReadAllBytes(pluginsTxt);
		var p = ProfileOf(f);
		Check(p.ActivePlugins().Contains(Name), "read by its real name: " + string.Join(", ", p.ActivePlugins()));
		p.DeactivatePlugins(["Output A"]);
		Check(!p.ActivePlugins().Contains("OutA.esp") && p.ActivePlugins().Contains(Name), "(the file was written again, another plugin unticked)");
		Check(File.ReadAllBytes(pluginsTxt).AsSpan().IndexOf(ansi.GetBytes("*" + Name + "\r\n")) >= 0, "its line is as Mod Organizer wrote it, byte for byte");
		p.ActivatePlugins(["Output A"]);
		Check(File.ReadAllBytes(pluginsTxt).AsSpan().SequenceEqual(before), "ticked again, the whole file is as it was, byte for byte");
		// a plugin new to both files: both are written again, each in its own way
		f.Mod("Fresh", ("Fresh.esp", false, []));
		p.ActivatePlugins(["Fresh"]);
		var plugins = File.ReadAllBytes(pluginsTxt);
		Check(plugins.AsSpan().IndexOf(ansi.GetBytes("*" + Name + "\r\n")) >= 0 && plugins.AsSpan().IndexOf("*Fresh.esp"u8) >= 0, "plugins.txt: the new plugin ticked, the accented one as it was");
		var order = File.ReadAllBytes(loadOrder);
		Check(order.AsSpan().IndexOf(new UTF8Encoding(false).GetBytes(Name + "\r\n")) >= 0 && order.AsSpan().IndexOf("Fresh.esp"u8) >= 0, "loadorder.txt: the new plugin listed, the accented one still UTF-8");
	}

	// Mod Organizer's settings file as Qt writes it: a character it will not write plainly is \x and its number, as many hex
	// digits as follow; the game's folder is UTF-8 bytes in a byte array. Read that way, and written that way when a tool is added
	static void T_QtEscapesAreReadAndWrittenAsQtDoes()
	{
		var ini = Mo2Instance.ReadQtIni("""
			[General]
			gamePath=@ByteArray(D:\\Jeux\\Skyrim \xc3\x89\x64ition)
			[customExecutables]
			1\binary=D:/Outils/Mod\xe9s/x.exe
			1\title=\x436\x43e\x434 tool
			1\arguments=-o:\"D:\\LOD\\\" a\tb
			""");
		Equal(@"D:\Jeux\Skyrim Édition", ini["General"]["gamePath"], "the game's folder with an accent (UTF-8 bytes, the letter after them escaped too)");
		Equal("D:/Outils/Modés/x.exe", ini["customExecutables"]["1\\binary"], "a tool's path with an accent");
		Equal("жод tool", ini["customExecutables"]["1\\title"], "letters outside Latin");
		Equal("-o:\"D:\\LOD\\\" a\tb", ini["customExecutables"]["1\\arguments"], "quotes, backslashes and a tab");
		foreach (var value in new[] { "D:/Outils/Modés/décor.exe", "D:/ж/a b.exe", "a\"b\\c", "x,y; z=1", "plain" }) {
			var written = ToolFinder.QtText(value);
			Check(written.All(c => c is >= ' ' and <= '~'), "written in plain ASCII: " + written);
			Equal(value, Mo2Instance.ReadQtIni("[s]\nk=" + written)["s"]["k"], "and read back as it was");
		}
		Equal(@"D:/Outils/Mod\xe9s/d\xe9\x63or.exe", ToolFinder.QtText("D:/Outils/Modés/décor.exe"), "the letter after an escape is escaped when it is a hex digit");
	}

	// the GOG copy is known by what it ships, never by the word in a folder's name
	static void T_AGogGameIsKnownByItsFilesNotItsFolder()
	{
		using var f = new FakeMo2(a_deeper: "GOG Games");
		var setup = f.WriteSetup(("landscapefixer", []));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Check(mo2.GameDir.Contains("GOG", StringComparison.Ordinal), "(the game stands in a folder named GOG)");
		Check(!InGame.SkseLogs(setup, mo2).Contains("Special Edition GOG", StringComparison.Ordinal), "its SKSE logs are not looked for in the GOG folder");
		Check(!InGame.SavesDir(ProfileOf(f)).Contains("Special Edition GOG", StringComparison.Ordinal), "nor its saves");
		File.WriteAllText(Path.Combine(f.Game, "Galaxy64.dll"), "");
		Check(InGame.SavesDir(ProfileOf(f)).Contains("Special Edition GOG", StringComparison.Ordinal), "a copy that ships GOG's own library reads the GOG folder");
	}

	// FasterNGIO's wrapper reaches a program in a folder with an accent and a non-Latin letter, and only its own last line is
	// the end of the run
	static void T_TheGrassWrapperReadsAnyPathAndItsOwnLastLine()
	{
		using var f = new FakeMo2();
		var work = Path.Combine(Path.GetTempPath(), "trwrap" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(work);
		try {
			// a stand-in program in the odd folder: Windows' own command processor under another name
			var exe = Path.Combine(f.Root, "outil modé ж.exe");
			File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
			var outFile = Path.Combine(work, "output.txt");
			var wrapper = Path.Combine(work, "run.cmd");
			Files.WriteAtomic(wrapper, GrassStep.WrapperText(exe, "/c echo it ran", outFile), new UTF8Encoding(false));
			using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c \"" + wrapper + "\"") { CreateNoWindow = true, UseShellExecute = false })!) {
				Check(p.WaitForExit(30000), "(the wrapper ended)");
			}
			var said = Files.ReadSharedText(outFile);
			Equal("0", GrassStep.WrapperExit(said), "the program in the odd folder was found and ran: " + said);
			Check(said.Contains("it ran", StringComparison.Ordinal), "and what it printed is kept");
		} finally {
			Directory.Delete(work, true);
		}
		Equal<string?>(null, GrassStep.WrapperExit("started\r\nworker 3 ended with exit code 0, going on\r\n"), "the words in a line of the tool's own are not the end");
		Equal("-1073741819", GrassStep.WrapperExit("started\r\nworking\r\nexit code -1073741819 \r\n"), "the wrapper's own last line is, with its number");
	}

	// An output a tool rewrites where it stands in its mod (nothing new lands in overwrite): its old bytes are kept first. The
	// run that finished keeps the new one, an undo puts the old one back with the rest of the mod left alone, and a step that
	// fails half way gets the old one back
	static void T_AnOutputRewrittenInPlaceIsKeptFirst()
	{
		using var f = new FakeMo2();
		var patch = Path.Combine(f.Mods, "Output A", "Synthesis.esp");
		File.WriteAllText(patch, "the old patch");
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			StepKit.KeepInPlace(ctx, s.OutputMod!, "Synthesis.esp");
			File.WriteAllText(patch, "the new patch");
			StepKit.Settled(ctx, s.OutputMod!, [patch]);
			ctx.SetAfter(s.OutputMod!);
		}));
		var setup = f.WriteSetup(("synthesis", new JsonObject { ["output_mod"] = "Output A" }));
		setup.Root["zipBackups"] = false;
		setup.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		Equal("the new patch", File.ReadAllText(patch), "the new patch stands after the run");
		var undo = Runner.Undo(Journal.Load(r.Folder!));
		Equal(RunState.Restored, undo.State, "undone: " + undo.Verdict);
		Equal("the old patch", File.ReadAllText(patch), "an undo puts the old patch back");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "and the rest of the mod stays where it is");

		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			StepKit.KeepInPlace(ctx, s.OutputMod!, "Synthesis.esp");
			File.WriteAllText(patch, "half writ");
			throw new StepFailedException("the tool was closed half way");
		}));
		Thread.Sleep(1100);
		Equal(RunState.Failed, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run whose tool stopped half way");
		Equal("the old patch", File.ReadAllText(patch), "the half-written patch is replaced by the old one");
	}

	// The player's own cache in Mod Organizer's overwrite (the shader cache, the grass cache) comes back when the step that
	// moved it out fails; it used to stay in the run's backup, and went for good when the backups were pruned
	static void T_ThePlayersCacheInOverwriteComesBackWhenItsStepFails()
	{
		using var f = new FakeMo2();
		var cache = Path.Combine(f.Overwrite, "ShaderCache");
		Directory.CreateDirectory(cache);
		File.WriteAllText(Path.Combine(cache, "old.bin"), "the working cache");
		var fail = true;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.ParkDir(ctx, cache, "overwrite ShaderCache (before)", "landscapefixer");
			Check(!File.Exists(Path.Combine(cache, "old.bin")), "(moved out for the step)");
			File.WriteAllText(Path.Combine(cache, "new.bin"), "a new cache");
			if (fail) {
				throw new StepFailedException("the compile failed");
			}
		}));
		var setup = f.WriteSetup(("landscapefixer", []));
		setup.Root["zipBackups"] = false;
		setup.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "failed");
		Equal("the working cache", File.ReadAllText(Path.Combine(cache, "old.bin")), "the cache the player had is back in overwrite");
		Check(!File.Exists(Path.Combine(cache, "new.bin")), "the half-built one is out of it");
		Check(Directory.EnumerateFiles(f.Backups, "new.bin", SearchOption.AllDirectories).Any(), "and kept in the run's backup, never erased");
		Check(!r.Verdict.Contains("except", StringComparison.Ordinal), "the run says the profile is back as it was: " + r.Verdict);
		fail = false;
		Thread.Sleep(1100);
		var done = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, done.State, done.Verdict);
		Check(File.Exists(Path.Combine(cache, "new.bin")) && !File.Exists(Path.Combine(cache, "old.bin")), "a step that finished keeps what it made");
		Check(Journal.Load(done.Folder!).Parked.Single().Replaced, "and its record says the old cache was replaced");
	}

	// the learned settings of a step are never without a whole set: a new one replaces the old by two renames, and a Keep cut
	// off between them is taken up by the next look
	static void T_LearnedSettingsAreNeverWithoutAWholeSet()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A", ["executable"] = "SomeTool" }));
		var setup = Setup.Load(f.SetupFile);
		var live = Path.Combine(f.Root, "tool.ini");
		File.WriteAllText(live, "first");
		ToolSettings.Keep(setup, "landscapefixer", "SomeTool", [live]);
		var dir = ToolSettings.Folder(setup, "landscapefixer");
		Directory.Move(dir, dir + " new");
		Check(ToolSettings.Load(setup, "landscapefixer") != null && Directory.Exists(dir) && !Directory.Exists(dir + " new"), "a whole new set left beside the step's folder is taken up");
		Directory.Move(dir, dir + " old");
		Check(ToolSettings.Load(setup, "landscapefixer") != null && Directory.Exists(dir), "with only the old one left, it stands again");
		File.WriteAllText(live, "second");
		ToolSettings.Keep(setup, "landscapefixer", "SomeTool", [live]);
		Check(!Directory.Exists(dir + " old") && !Directory.Exists(dir + " new"), "a new set leaves no spare beside it");
		Equal("second", File.ReadAllText(Path.Combine(dir, ToolSettings.Load(setup, "landscapefixer")!.Files[0].Stored)), "and holds the new settings");
	}

	// A setup file comes from anywhere: what it gives as a mod's name must be one plain folder name (a path there once had a
	// folder of the player's emptied into the run's backup), an imported one brings no command to run, and a run's preview names
	// the commands a setup runs
	static void T_ASetupFromElsewhereCannotNameAFolderAsAMod()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		var bad = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = @"..\..\profiles\Test" }));
		Check(bad.Problems().Any(p => p.Contains("not a mod's name", StringComparison.Ordinal)), "said as a problem of the setup: " + string.Join("; ", bad.Problems()));
		var before = f.Text("modlist.txt");
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.State == RunState.Failed && r.Verdict.Contains("not a mod's name", StringComparison.Ordinal), "a run is refused: " + r.Verdict);
		Equal(before, f.Text("modlist.txt"), "the mod list was not touched");
		Check(!File.Exists(Path.Combine(f.ProfileDir, "meta.ini")), "and nothing was written into the folder it named");
		foreach (var name in new[] { @"C:\Users\x\Documents", "a/b", "..", ".", "ends with a dot.", "ends with a space ", "a:b", "" }) {
			Check(!Setup.PlainName(name), "not a mod's name: \"" + name + "\"");
		}
		foreach (var name in new[] { "Output A", "DynDOLOD Output - My Profile", "Größe (v1.2) ж", "PG_1" }) {
			Check(Setup.PlainName(name), "a mod's name: " + name);
		}
		var nested = f.WriteSetup(
			("shaders", new JsonObject { ["caches"] = new JsonArray(new JsonObject { ["shader_mod"] = "Shaders", ["output_mod"] = @"x\y" }) }),
			("landscapefixer", new JsonObject { ["output_mod"] = "Output A", ["replaces"] = new JsonArray("fine", @"..\up") }));
		Equal(2, nested.Problems().Count(p => p.Contains("not a mod's name", StringComparison.Ordinal)), "a cache's mod and a list's entry are checked too: " + string.Join("; ", nested.Problems()));

		var own = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		own.Root["beforeRun"] = new JsonArray("prepare.cmd");
		own.Root["afterRun"] = new JsonArray("restore.cmd", "tidy up");
		var lines = RunPreview.Build(own, Mo2Instance.Load(f.Exe, f.Instance), Runner.Plan(own, null, null, null)).Lines(_ => null);
		Check(lines.Any(l => l.Contains("before the first step: prepare.cmd", StringComparison.Ordinal)) && lines.Any(l => l.Contains("after the run: tidy up", StringComparison.Ordinal)),
			"the preview names the setup's own commands: " + string.Join(" | ", lines));
		Equal(3, own.TakeOutCommands(), "a setup brought in from elsewhere has its commands taken out");
		Check(own.BeforeRun.Count == 0 && own.AfterRun.Count == 0, "none is left");
	}

	// "run again from the failed step" is a new run: the place the player stood at before the clean save is taken from the run
	// that made that save (without it the final save was made in the test cell)
	static void T_ARetryFindsThePlaceTheCleanSaveCameFrom()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", []));
		var p = ProfileOf(f);
		File.WriteAllText(Path.Combine(p.Dir, "settings.ini"), "[General]\r\nLocalSaves=true\r\n");
		var saves = InGame.SavesDir(p);
		Directory.CreateDirectory(saves);
		var save = Path.Combine(saves, InGame.SaveClean + ".ess");
		File.WriteAllText(save, "");
		var written = DateTime.Now.AddHours(-2);
		File.SetLastWriteTime(save, written);
		var mine = Setup.Load(f.SetupFile);
		var made = new Journal { Id = "made", Folder = Path.Combine(Runner.RunsDir(f.Data), "2026-01-01 the run that made it"), Profile = "Test", State = RunState.Failed,
			Mo2Exe = mine.Mo2Exe, Mo2Instance = mine.Mo2Instance,
			Started = written.AddMinutes(-10), ReturnMarker = "FF000801",
			Steps = [new StepRecord { Key = "cleansave", State = StepState.Done, Started = written.AddMinutes(-8), Ended = written.AddSeconds(40) }] };
		Directory.CreateDirectory(made.Folder);
		made.Save();
		var ctx = StepContext(f, "landscapefixer");
		try {
			Equal<string?>("FF000801", InGame.MarkerOfCleanSave(ctx), "the place on record in the run whose clean save it is");
			// a profile of the same name under another Mod Organizer is another profile
			made.Mo2Instance = Path.Combine(f.Root, "Another MO2");
			made.Save();
			Equal<string?>(null, InGame.MarkerOfCleanSave(ctx), "never the place of a run of another Mod Organizer");
			made.Mo2Instance = mine.Mo2Instance;
			made.Save();
			File.SetLastWriteTime(save, written.AddHours(1));
			Equal<string?>(null, InGame.MarkerOfCleanSave(ctx), "never the place of a run that made another clean save");
		} finally {
			ctx.Log.Dispose();
		}
	}

	// what a tool or the game does is said in words: one that ends the moment it started, one that stops while it loads, an add-on
	// whose program was swapped under an unchanged file, a small cache that is still whole
	static void T_WhatAToolDidIsSaidInWords()
	{
		Throws<StepFailedException>(() => Mo2Control.Open(999999, "The tool").Dispose(), "a tool that ended at once");
		try {
			Mo2Control.Open(999999, "The tool").Dispose();
		} catch (StepFailedException e) {
			Check(e.Message.Contains("The tool started and ended at once", StringComparison.Ordinal), "in words, never .NET's own: " + e.Message);
		}
		Equal<string?>("[00:03] Background Loader: Fatal: <EOSError: System Error.  Code: 2.>", XLodGenStep.LoadError("[00:01] Background Loader: loading \"Skyrim.esm\"...\r\n[00:03] Background Loader: Fatal: <EOSError: System Error.  Code: 2.>\r\n"),
			"the loader's own line when it stopped");
		Equal<string?>(null, XLodGenStep.LoadError("[00:01] Background Loader: loading \"Skyrim.esm\"...\r\n[00:09] Background Loader: finished\r\nErrors were found in 0 records\r\n"), "none in a log of a load that went well");

		using var f = new FakeMo2();
		var modDir = Path.Combine(f.Mods, "Base Mod");
		Directory.CreateDirectory(Path.Combine(modDir, "tools"));
		Directory.CreateDirectory(Path.Combine(modDir, Brand.Name));
		var program = Path.Combine(modDir, "tools", "MyPatcher.exe");
		File.WriteAllText(program, "the program the player allowed");
		var file = Path.Combine(modDir, Brand.Name, "my patcher.json");
		File.WriteAllText(file, """{ "schema": 1, "name": "My Patcher", "author": "A Modder", "program": "tools/MyPatcher.exe", "output": "My Patcher Output" }""");
		var allowed = AddOns.Read(file, modDir)!;
		Check(allowed.Says.Contains("KB, last changed", StringComparison.Ordinal), "the question shows the program's size and day: " + allowed.Says);
		File.WriteAllText(program, "another program under the same file");
		Check(AddOns.Read(file, modDir)!.Hash != allowed.Hash, "a program swapped under an unchanged add-on file is not what was allowed");
		File.WriteAllText(program, "the program the player allowed");
		Equal(allowed.Hash, AddOns.Read(file, modDir)!.Hash, "the same file and the same program: the same fingerprint");
	}

	// A game with no shader mod has no shader compile to wait for (a profile on ENB sat five minutes at the main menu at each
	// start); a plugin's flags are read from its file again only when the file changed; a log's last lines are read without the
	// megabytes before them
	static void T_NothingIsWaitedForOrReadThatIsNotThere()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", []));
		var ctx = StepContext(f, "landscapefixer");
		var logs = Path.Combine(f.Root, "SKSE logs");
		Directory.CreateDirectory(logs);
		try {
			var started = DateTime.Now;
			Check(!InGame.ShaderModHere(ctx, [logs], started), "no shader plugin, no shader log: nothing to wait for");
			var plugins = Path.Combine(f.Mods, "Base Mod", "SKSE", "Plugins");
			Directory.CreateDirectory(plugins);
			File.WriteAllText(Path.Combine(plugins, "CommunityShaders.dll"), "");
			Check(InGame.ShaderModHere(ctx, [logs], started), "a ticked mod ships the shader plugin");
			File.Delete(Path.Combine(plugins, "CommunityShaders.dll"));
			File.WriteAllText(Path.Combine(plugins, "SomethingElse.dll"), "");
			Check(!InGame.ShaderModHere(ctx, [logs], started), "another plugin is no shader mod");
			File.WriteAllText(Path.Combine(logs, "OpenShaders.log"), "started");
			Check(InGame.ShaderModHere(ctx, [logs], started), "a shader log this start wrote says there is one, whatever its plugin is called");
			Check(!InGame.ShaderModHere(ctx, [logs], DateTime.Now.AddHours(1)), "a log from before this start says nothing");
		} finally {
			ctx.Log.Dispose();
		}

		var plugin = Path.Combine(f.Root, "Flagged.esp");
		FakeMo2.Plugin(plugin, true);
		Check(Plugins.IsMaster(plugin) && Plugins.IsMaster(plugin), "a master-flagged plugin, asked twice");
		FakeMo2.Plugin(plugin, false);
		File.SetLastWriteTimeUtc(plugin, DateTime.UtcNow.AddSeconds(5));
		Check(!Plugins.IsMaster(plugin), "the file changed: its flag is read again, not remembered");

		var log = Path.Combine(f.Root, "big.log");
		File.WriteAllText(log, string.Concat(Enumerable.Range(0, 20000).Select(i => $"[12:00:00] line {i} of a long shader log\n")) + "the last line\n");
		var tail = Files.ReadSharedTailText(log, 4096);
		Check(tail.Length <= 4096 && tail.EndsWith("the last line\n", StringComparison.Ordinal) && tail.StartsWith("[12:00:00] line ", StringComparison.Ordinal), "the end of a long log, from a whole line: " + tail[..Math.Min(60, tail.Length)]);
		var brief = Path.Combine(f.Root, "short.log");
		File.WriteAllText(brief, "short\n");
		Equal("short\n", Files.ReadSharedTailText(brief, 4096), "a short one is read whole");
	}

	// ------------------------------------------------------------------ Tool Runner 1.0: rotation, the forecast, the catalog, the dry run, the summary, status, JSON
	// a file the program only ever adds to (its crash notes) is kept to a size: the older lines move beside it, the one before that goes
	static void T_AFileThatOnlyGrowsIsRolled()
	{
		using var f = new FakeMo2();
		var notes = Path.Combine(f.Root, "crash.txt");
		var older = Path.Combine(f.Root, "crash (older).txt");
		Files.AppendCapped(notes, new string('a', 600) + "\n", 1000);
		Check(!File.Exists(older), "under its size nothing moves");
		Files.AppendCapped(notes, new string('b', 600) + "\n", 1000);
		Check(File.Exists(older) && File.ReadAllText(older).StartsWith('a'), "past its size the old notes move beside it");
		Check(File.ReadAllText(notes).StartsWith('b') && new FileInfo(notes).Length < 1000, "and the file starts again with the newest");
		Files.AppendCapped(notes, new string('c', 600) + "\n", 1000);
		Check(File.ReadAllText(older).StartsWith('b') && File.ReadAllText(notes).StartsWith('c'), "the one before goes: the two never hold more than twice the size");
		// held by another program right now: added to as it is (never a lost note), rolled the next time
		using (new FileStream(notes, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
			Files.AppendCapped(notes, new string('d', 600) + "\n", 1000);
		}
		Check(File.ReadAllText(notes).StartsWith('c') && File.ReadAllText(notes).Contains('d'), "a held file still takes the note");
		Files.AppendCapped(notes, "e\n", 1000);
		Check(File.ReadAllText(notes) == "e\n" && File.ReadAllText(older).Contains('d'), "and is rolled once it is free");
		Equal(512L * 1024, ToolRunner.App.App.CrashNotesMost, "the crash notes are kept to half a megabyte");
	}

	// the bug reports a player made pile up in a folder they never open: only the newest few of the program's own stay
	static void T_OnlyTheNewestBugReportsStay()
	{
		using var f = new FakeMo2();
		var dir = Path.Combine(f.Root, "Bug reports");
		Directory.CreateDirectory(dir);
		for (var d = 1; d <= 12; ++d) {
			var old = Path.Combine(dir, $"{(d % 2 == 0 ? Brand.Name : Brand.OldNames[0])} bug report 2026-01-{d:00} 00-00-00.zip");
			File.WriteAllText(old, "x");
			File.SetLastWriteTimeUtc(old, new DateTime(2026, 1, d, 0, 0, 0, DateTimeKind.Utc));
		}
		File.WriteAllText(Path.Combine(dir, "my own notes.zip"), "x");
		var made = BugReport.Make(f.Data, null, dir, "1.0");
		var left = Directory.GetFiles(dir).Select(p => Path.GetFileName(p)).ToList();
		Equal(BugReport.Kept + 1, left.Count, "the newest of its own and the player's own file: " + string.Join(" | ", left));
		Check(left.Contains(Path.GetFileName(made)) && left.Contains("my own notes.zip"), "the new one stays, and a file that is not the program's is never touched");
		Check(!left.Any(n => n.Contains("2026-01-01", StringComparison.Ordinal) || n.Contains("2026-01-03", StringComparison.Ordinal)) && left.Any(n => n.Contains("2026-01-12", StringComparison.Ordinal)),
			"the oldest went, under whichever of its names they were made");
	}

	// the room a run takes, added up per disk: the new output where its tool writes it, the old one in the backup, the extras apart
	static void T_TheForecastAddsUpEachDisk()
	{
		using var f = new FakeMo2();
		var scratch = Path.Combine(f.Root, "Scratch");
		// Synthesis writes through Mod Organizer; xLODGen fills a folder ACMOS turns into a mod; DynDOLOD writes a folder of its own first
		var setup = f.WriteSetup(
			("synthesis", new JsonObject { ["output_mod"] = "Output A" }),
			("xlodgen", new JsonObject { ["output_dir"] = Path.Combine(scratch, "lod") }),
			("acmos", new JsonObject { ["output_mod"] = "Output B", ["reads"] = Path.Combine(scratch, "lod") }),
			("dyndolod", new JsonObject { ["output_mod"] = "Needs B", ["output_dir"] = Path.Combine(scratch, "dyn") }),
			("texgen", new JsonObject { ["output_mod"] = "Never Built", ["output_dir"] = Path.Combine(scratch, "tex") }));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		const long gb = 1L << 30;
		var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [mo2.ModDir("Output A")] = 2 * gb, [mo2.ModDir("Output B")] = 10 * gb, [mo2.ModDir("Needs B")] = 20 * gb };
		long Size(string a_dir) => sizes.GetValueOrDefault(a_dir);
		static long Grown(long a_size) => a_size + a_size * 10 / 100;
		var free = 100 * gb;
		DiskForecast On(Func<string, string> a_volume, IReadOnlyList<Setup.StepEntry>? a_todo = null) =>
			DiskForecast.Build(setup, mo2, a_todo ?? setup.EnabledSteps, new DiskForecast.Probes(a_volume, _ => free, Size));

		// everything on one disk: one more copy of each output it remakes
		var one = On(_ => "X");
		Equal(1, one.Disks.Count, "one disk");
		Equal(Grown(2 * gb) + Grown(10 * gb) + Grown(20 * gb), one.Disks[0].Needs, "the three outputs, each a tenth bigger than last time");
		Equal(one.Disks[0].Needs + 32 * gb, one.Disks[0].Wants, "and the pack at the end wants the old ones' size once more");
		Check(one.NotKnown.SequenceEqual(["Never Built"]), "an output never built is named, not guessed: " + string.Join(", ", one.NotKnown));
		Check(!one.Issues().Any(), "100 GB free: nothing to say");
		free = 40 * gb;
		var tight = one = On(_ => "X");
		Check(tight.Issues().Single() is { Blocking: false } note && note.Text.Contains("packing the old outputs", StringComparison.Ordinal), "room for the run but not its extras: told, never stopped");
		free = 30 * gb;
		var full = On(_ => "X").Issues().ToList();
		Check(full.Single() is { Blocking: true } stop && stop.Text.Contains("not enough room on", StringComparison.Ordinal) && stop.Text.Contains(DiskForecast.Size(30 * gb) + " is free", StringComparison.Ordinal), "a run that will not fit is stopped, with the numbers");
		Check(On(_ => "X").Issues(false).All(i => !i.Blocking), "a setup that asks to be told only is not stopped");
		free = 36 * gb;
		Check(On(_ => "X").Issues().Any(i => !i.Blocking && i.Text.Contains("nearly full", StringComparison.Ordinal)) || On(_ => "X").Issues().Any(i => i.Text.Contains("leaves out", StringComparison.Ordinal)),
			"a disk that only just holds the run is told");
		free = 100 * gb;

		// the backup on a disk of its own: the old outputs are copied there, and the mods' disk needs room only where a tool writes
		string Apart(string a_p) => Files.IsUnder(a_p, f.Backups) ? "B" : "M";
		var two = On(Apart);
		var b = two.Disks.Single(d => d.Key == "B");
		var m = two.Disks.Single(d => d.Key == "M");
		Equal(32 * gb, b.Needs, "the old outputs, kept in the backup");
		Equal(64 * gb, b.Wants, "and the pack beside them");
		Equal(Grown(10 * gb) + Grown(20 * gb), m.Needs, "the two tools that write a folder of their own first");
		Equal(m.Needs + Grown(2 * gb), m.Wants, "a tool that writes through Mod Organizer may need its room for a moment: wanted, never what stops a start");

		// the tools' folders on a third disk
		string Three(string a_p) => Files.IsUnder(a_p, f.Backups) ? "B" : Files.IsUnder(a_p, scratch) ? "S" : "M";
		var three = On(Three);
		Equal(Grown(10 * gb) + Grown(20 * gb), three.Disks.Single(d => d.Key == "S").Needs, "what the tools write");
		Equal(0L, three.Disks.Single(d => d.Key == "M").Needs, "the mods' disk only swaps old for new");

		// the tools' folders apart, the backup beside the mods: the new output lands beside the old one
		string Two(string a_p) => Files.IsUnder(a_p, scratch) ? "S" : "M";
		var beside = On(Two);
		Equal(Grown(2 * gb) + Grown(10 * gb) + Grown(20 * gb), beside.Disks.Single(d => d.Key == "M").Needs, "every new output beside its old one");

		// xLODGen run alone still counts the terrain LOD it writes (the mod is the road painter's)
		var alone = On(Three, setup.EnabledSteps.Where(s => s.Key == "xlodgen").ToList());
		Equal(Grown(10 * gb), alone.Disks.Single(d => d.Key == "S").Needs, "its folder fills with a terrain LOD the size of the last one");

		var lines = one.Lines();
		Check(lines[0].StartsWith("Room on disk", StringComparison.Ordinal) && lines.Any(l => l.Contains("not known yet for Never Built", StringComparison.Ordinal)), "in words: " + string.Join(" | ", lines));
	}

	// the check before a run stops one that will not fit - unless the setup asks to be told only
	static void T_ARunThatWillNotFitIsNotStarted()
	{
		using var f = new FakeMo2();
		var ran = false;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			ran = true;
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var keep = DiskForecast.Real;
		try {
			// the output was 8 GB the last time, and its disk has 5 GB
			DiskForecast.Real = keep with { SizeOf = _ => 8L << 30, Free = _ => 5L << 30 };
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(r.Folder == null && !ran && r.Verdict.Contains("not enough room on", StringComparison.Ordinal), "refused before anything ran: " + r.Verdict);
			Equal("TR-205", Explain.For("", r.Verdict)?.Code, "and the catalog says what to do");
			setup.Root["diskForecastStops"] = false;
			setup.Save();
			r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(r.State == RunState.Finished && ran, "told only: the run goes ahead: " + r.Verdict);
		} finally {
			DiskForecast.Real = keep;
		}
	}

	// what a step built is measured once it is done: on its record, and kept beside the setup for the next forecast
	static void T_AnOutputsSizeIsRememberedForTheNextForecast()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			File.WriteAllBytes(Path.Combine(ctx.Mo2.ModDir("Output A"), "big.bin"), new byte[300_000]);
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		Check(Journal.Load(r.Folder!).Step("landscapefixer")!.OutputBytes >= 300_000, "the step's record carries its output's size");
		var setup = Setup.Load(f.SetupFile);
		var asked = 0;
		var next = DiskForecast.Build(setup, Mo2Instance.Load(f.Exe, f.Instance), setup.EnabledSteps, DiskForecast.Real with { SizeOf = _ => {
			++asked;
			return 0;
		} });
		Equal(0, asked, "the next forecast reads the number: the folder is not walked again");
		Check(next.Disks.Sum(d => d.Needs) >= 300_000 && next.NotKnown.Count == 0, "and it is what the forecast counts");
	}

	// ---- the catalog of the runner's own refusals and failures
	// every piece of text in C# source from a_at (the first character of a call's arguments) to the bracket that closes the call:
	// the call's arguments as written (strings left out), and each string in it with a gap where a value is put in
	static (string Args, List<string> Strings, int End) ReadCall(string a_src, int a_at)
	{
		var args = new StringBuilder();
		var strings = new List<string>();
		var depth = 1;
		var i = a_at;
		// one string, from its opening quote on; a_hole: braces hold code
		string Str(bool a_hole, bool a_verbatim)
		{
			var s = new StringBuilder();
			for (++i; i < a_src.Length; ++i) {
				var c = a_src[i];
				if (a_verbatim && c == '"' && i + 1 < a_src.Length && a_src[i + 1] == '"') {
					s.Append('"');
					++i;
				} else if (!a_verbatim && c == '\\') {
					s.Append(a_src[++i] == '"' ? '"' : ' ');
				} else if (c == '"') {
					break;
				} else if (a_hole && c == '{' && a_src[i + 1] == '{') {
					s.Append('{');
					++i;
				} else if (a_hole && c == '{') {
					s.Append(" ... ");
					for (var open = 1; open > 0;) {
						c = a_src[++i];
						if (c == '"') {
							for (++i; a_src[i] != '"'; ++i) {
								if (a_src[i] == '\\') {
									++i;
								}
							}
						} else if (c == '{') {
							++open;
						} else if (c == '}') {
							--open;
						}
					}
				} else {
					s.Append(c);
				}
			}
			return s.ToString();
		}
		for (; i < a_src.Length && depth > 0; ++i) {
			var c = a_src[i];
			if (c == '/' && a_src[i + 1] == '/') {
				var end = a_src.IndexOf('\n', i);
				i = end < 0 ? a_src.Length : end;
			} else if (c == '\'') {
				i += a_src[i + 1] == '\\' ? 3 : 2;
			} else if (c == '"' && a_src.AsSpan(i).StartsWith("\"\"\"")) {
				i = a_src.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal) + 2;
			} else if (c is '$' or '@' && (a_src[i + 1] == '"' || (a_src[i + 1] is '$' or '@' && a_src[i + 2] == '"'))) {
				var mark = a_src[i + 1] == '"' ? a_src.Substring(i, 1) : a_src.Substring(i, 2);
				// on to the string's opening quote
				i += mark.Length;
				strings.Add(Str(mark.Contains('$'), mark.Contains('@')));
			} else if (c == '"') {
				strings.Add(Str(false, false));
			} else {
				depth += c == '(' ? 1 : c == ')' ? -1 : 0;
				if (depth > 0) {
					args.Append(c);
				}
			}
		}
		return (args.ToString(), strings, i);
	}

	// every refusal of a start and every stopping finding of the check in a_core's source that the catalog has no entry for
	static List<string> RefusalsNotInTheCatalog(string a_core, out int a_seen)
	{
		var missing = new List<string>();
		a_seen = 0;
		foreach (var file in Directory.EnumerateFiles(a_core, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains(@"\obj\", StringComparison.Ordinal) && !p.Contains(@"\bin\", StringComparison.Ordinal))) {
			var src = File.ReadAllText(file);
			foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(src, @"\bnew (?:Steps\.)?Issue\(|\bRefuse\(|\.No\(")) {
				// a call named in a comment is no call
				var line = src.LastIndexOf('\n', call.Index) + 1;
				if (src.AsSpan(line, call.Index - line).Contains("//", StringComparison.Ordinal)) {
					continue;
				}
				var (args, strings, _) = ReadCall(src, call.Index + call.Length);
				if (call.Value.EndsWith("Issue(", StringComparison.Ordinal)) {
					// only what stops a run: its second argument says so
					var second = args.Split(',').Skip(1).FirstOrDefault()?.Trim();
					if (second is not ("true" or "a_stops" or "a_blocking")) {
						continue;
					}
				}
				// the declaration itself and a call that only hands a text on (an error's own message) hold no words of their own
				var text = string.Join(" ", strings.Where(s => s.Trim().Length > 0)).Trim();
				if (text.Length < 8) {
					continue;
				}
				++a_seen;
				if (RunnerErrors.Find(text) == null) {
					missing.Add($"{Path.GetFileName(file)}: {text}");
				}
			}
		}
		return missing;
	}

	// FasterNGIO runs through a command line written from the setup: nothing in its program path or its arguments may be read
	// there as a command of its own (a setup file can come from anyone)
	static void T_TheGrassStepRunsNoCommandHiddenInItsSettings()
	{
		const string exe = @"C:\Tools\FasterNGIO\FasterNGIO.exe";
		Equal<string?>(null, GrassStep.WrapperRefuses(exe, null), "no arguments: its own");
		Equal<string?>(null, GrassStep.WrapperRefuses(exe, "--world all"), "plain arguments");
		Equal<string?>(null, GrassStep.WrapperRefuses(exe, "--world \"Tamriel\""), "a quoted value is still an argument");
		foreach (var bad in new[] { "--world all & calc", "--world all | more", "--world all > x.txt", "--world all < x", "--world ^all", "--world all\r\ndel x", "--world all\nx" }) {
			Check(GrassStep.WrapperRefuses(exe, bad) is { } why && RunnerErrors.Find(why)?.Code == "TR-317", "refused, with what to do: " + bad.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal));
		}
		Check(GrassStep.WrapperRefuses(@"C:\Tools\Fast""&calc&"".exe", null) != null, "a program path that closes its own quotes");
		Check(GrassStep.WrapperRefuses(@"C:\A & B\FasterNGIO.exe", null) != null, "a program path that holds a command mark");
	}

	// the setup's own name names its run's folder and its backup: one with a folder part never reaches them
	static void T_ASetupNamedWithAFolderPartIsRefused()
	{
		var problems = Setup.FromJson(new JsonObject { ["name"] = @"..\..\Elsewhere" }).Problems();
		Check(problems.FirstOrDefault(p => p.Contains("not a plain name", StringComparison.Ordinal)) is { } said && RunnerErrors.Find(said)?.Code == "TR-120", "refused by the setup's own check: " + string.Join(" | ", problems));
		Check(!Setup.FromJson(new JsonObject { ["name"] = "My Profile - LOD" }).Problems().Any(p => p.Contains("not a plain name", StringComparison.Ordinal)), "a plain name passes");
		// a setup brought in is kept under a name that passes the same test
		Equal("Example", ToolRunner.App.Cli.ImportName("Example. "), "a dot or a space at a brought-in name's end is dropped");
		Equal("My-Profile", ToolRunner.App.Cli.ImportName(@"C:\Elsewhere\My:Profile"), "folder parts go, and what Windows refuses becomes a dash");
		Equal<string?>(null, ToolRunner.App.Cli.ImportName(".."), "and a name with nothing plain in it is none");
		Check(new[] { "Example", "My-Profile" }.All(Setup.PlainName), "what it gives is a plain name");
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		setup.Root["name"] = @"..\..\Elsewhere";
		setup.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.State == RunState.Failed && r.Folder == null && r.Verdict.Contains("not a plain name", StringComparison.Ordinal), "the run is refused before anything is made: " + r.Verdict);
		Check(!Directory.EnumerateDirectories(f.Root, "*Elsewhere*", SearchOption.AllDirectories).Any(), "no folder of that name anywhere");
	}

	// what a plugin holds is read off its headers alone: its groups, a tree among its statics, a landscape inside a worldspace; a
	// file cut short gives what could be read, and a file that is no plugin gives nothing
	static void T_APluginsKindsAreReadOffItsHeaders()
	{
		static byte[] Rec(string a_type, uint a_flags, int a_data) =>
			[.. Encoding.ASCII.GetBytes(a_type), .. BitConverter.GetBytes((uint)a_data), .. BitConverter.GetBytes(a_flags), .. new byte[12], .. new byte[a_data]];
		static byte[] Grup(string a_label, params byte[][] a_inside)
		{
			var body = a_inside.SelectMany(b => b).ToArray();
			return [.. Encoding.ASCII.GetBytes("GRUP"), .. BitConverter.GetBytes((uint)(24 + body.Length)), .. Encoding.ASCII.GetBytes(a_label), .. new byte[12], .. body];
		}
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "kinds " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		try {
			var plain = Path.Combine(dir, "Plain.esp");
			File.WriteAllBytes(plain, [.. Rec("TES4", 0, 10), .. Grup("STAT", Rec("STAT", 0, 7)), .. Grup("WRLD", Rec("WRLD", 0, 3), Grup("\0\0\0\0", Rec("CELL", 0, 0)))]);
			var kinds = Plugins.Kinds(plain, true);
			Check(kinds.SetEquals(["STAT", "WRLD"]), "statics with no tree and a worldspace with no landscape: " + string.Join(",", kinds.Order(StringComparer.Ordinal)));
			var rich = Path.Combine(dir, "Rich.ESM");
			byte[] richBytes = [.. Rec("TES4", 0, 10), .. Grup("GRAS", Rec("GRAS", 0, 4)), .. Grup("STAT", Rec("STAT", 0, 7), Rec("STAT", 0x40, 2)),
				.. Grup("WRLD", Rec("WRLD", 0, 3), Grup("\0\0\0\0", Rec("CELL", 0, 0), Grup("\0\0\0\0", Rec("REFR", 0, 5), Rec("LAND", 0, 9))))];
			File.WriteAllBytes(rich, richBytes);
			kinds = Plugins.Kinds(rich, true);
			Check(kinds.SetEquals(["GRAS", "STAT", "TREE", "WRLD", "LAND"]), "a tree among the statics and a landscape in the worldspace: " + string.Join(",", kinds.Order(StringComparer.Ordinal)));
			Check(!Plugins.Kinds(rich, false).Contains("LAND"), "a landscape is looked for only when asked");
			Check(Plugins.TopGroups(rich).SetEquals(["GRAS", "STAT", "WRLD"]), "the top groups alone");
			var cut = Path.Combine(dir, "Cut.esp");
			File.WriteAllBytes(cut, richBytes[..^12]);
			kinds = Plugins.Kinds(cut, true);
			Check(kinds.Contains("GRAS") && kinds.Contains("TREE") && kinds.Contains("WRLD") && !kinds.Contains("LAND"), "a plugin cut short gives what could be read: " + string.Join(",", kinds.Order(StringComparer.Ordinal)));
			var none = Path.Combine(dir, "Not one.esp");
			File.WriteAllText(none, "this is no plugin, only a file named like one");
			Equal(0, Plugins.Kinds(none, true).Count, "a file that is no plugin holds nothing");
			Equal(0, Plugins.TopGroups(none).Count, "nor any group");
			File.WriteAllText(Path.Combine(dir, "readme.esp.txt"), "x");
			Check(Plugins.IsPlugin("a.ESP") && Plugins.IsPlugin("b.esl") && Plugins.IsPlugin("c.Esm") && !Plugins.IsPlugin("d.esp.bak") && !Plugins.IsPlugin("e.bsa"), "a plugin by the end of its name");
			Equal("Cut.esp|Not one.esp|Plain.esp|Rich.ESM", string.Join("|", Plugins.InMod(dir).Order(StringComparer.Ordinal)), "the plugins in a mod's folder");
			Equal(0, Plugins.InMod(Path.Combine(dir, "not there")).Count, "a folder that is not there holds none");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// small things each said once: a release file under the name GitHub gives it, an output's name from its pattern, a version
	// with a number in it, a mod's page off Mod Organizer's own record
	static void T_SmallNamesAndPages()
	{
		Check(ProgramUpdate.SameAssetName("Tool.Runner.1.1.zip", "Tool Runner 1.1.zip"), "GitHub's dots for our spaces");
		Check(ProgramUpdate.SameAssetName("tool.runner.1.1.ZIP", "Tool Runner 1.1.zip"), "case does not count");
		Check(!ProgramUpdate.SameAssetName("Tool.Runner.1.10.zip", "Tool Runner 1.1.zip"), "another version is another file");
		Check(!ProgramUpdate.SameAssetName(null, "Tool Runner 1.1.zip"), "no name is no match");
		Equal("DynDOLOD Output - My Profile", SetupMaker.OutputName("{output} - {profile}", "DynDOLOD Output", "My Profile"), "the usual pattern");
		Equal("My Profile (DynDOLOD Output)", SetupMaker.OutputName("{PROFILE} ({Output})", "DynDOLOD Output", "My Profile"), "a pattern of the player's own, however it is cased");
		Check(ToolUpdates.HasVersion("3.0.0.217") && ToolUpdates.HasVersion("Alpha-217") && !ToolUpdates.HasVersion("unknown") && !ToolUpdates.HasVersion(""), "a version has a number in it");
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "pages " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		try {
			Equal<string?>(null, LodGuide.NexusPage(dir), "a mod with no record has no page");
			var meta = Path.Combine(dir, "meta.ini");
			File.WriteAllText(meta, "[General]\r\ngameName=SkyrimSE\r\nmodid=68518\r\nversion=3.0.0.0\r\n[installedFiles]\r\n1\\modid=1\r\n");
			Equal<string?>("https://www.nexusmods.com/skyrimspecialedition/mods/68518", LodGuide.NexusPage(dir), "its page on Nexus");
			File.WriteAllText(meta, "[General]\r\ngameName=Skyrim\r\nmodid=12\r\n");
			Equal<string?>("https://www.nexusmods.com/skyrim/mods/12", LodGuide.NexusPage(dir), "a mod made for the older game keeps its own page");
			File.WriteAllText(meta, "[General]\r\ngameName=SkyrimSE\r\nmodid=0\r\n");
			Equal<string?>(null, LodGuide.NexusPage(dir), "a mod installed by hand (no number) has none");
			File.WriteAllText(meta, "[General]\r\ngameName=Fallout4\r\nmodid=7\r\n");
			Equal<string?>(null, LodGuide.NexusPage(dir), "nor one of a game it does not know");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// a link to another folder (a junction), inside a folder the run handles or as that folder itself: what it points at is never
	// counted, changed or emptied behind the player's back, and a tool's folder that is a link keeps its place
	static void T_ALinkToAnotherFolderIsLeftAsItIs()
	{
		using var f = new FakeMo2();
		// (made by Windows' own call, never a command processor: a "%" in the temp folder's name stays a "%")
		static bool Junction(string a_link, string a_to)
		{
			Program.Junction(a_link, a_to);
			return Directory.Exists(a_link);
		}
		var kept = Path.Combine(f.Root, "Kept elsewhere");
		var holds = Path.Combine(f.Root, "Holds a link");
		Directory.CreateDirectory(kept);
		Directory.CreateDirectory(Path.Combine(holds, "sub"));
		var theirs = Path.Combine(kept, "theirs.txt");
		var own = Path.Combine(holds, "sub", "own.txt");
		File.WriteAllText(theirs, "12345");
		File.WriteAllText(own, "123");
		if (!Junction(Path.Combine(holds, "link"), kept)) {
			Console.WriteLine("      (no junction could be made here: skipped)");
			return;
		}
		File.SetAttributes(theirs, FileAttributes.ReadOnly);
		try {
			Equal(1, Files.CountFiles(holds), "the files behind a link are not counted");
			Equal(3L, Files.SizeOf(holds), "nor their size");
			Equal(1, Files.LinksUnder(holds).Count, "the link is named");
			// a read-only file of the folder's own does not stop its removal; the one behind the link keeps its mark
			File.SetAttributes(own, FileAttributes.ReadOnly);
			Files.DeleteDir(holds);
			Check(!Directory.Exists(holds), "the folder is gone");
			Check(File.Exists(theirs) && File.GetAttributes(theirs).HasFlag(FileAttributes.ReadOnly), "the file behind the link is there, read-only as it was");
			// a folder that is itself a link is removed as the link
			var gone = Path.Combine(f.Root, "A link to remove");
			Check(Junction(gone, kept), "a junction to remove");
			Files.DeleteDir(gone);
			Check(!Directory.Exists(gone) && File.Exists(theirs), "removed as a link, what it points at untouched");
			// a folder that is itself a link moves as the link
			var link = Path.Combine(f.Root, "A link");
			Check(Junction(link, kept), "a second junction");
			var moved = Path.Combine(f.Root, "Moved", "A link");
			Files.MoveDirectory(link, moved);
			Check(!Directory.Exists(link) && Files.IsLink(moved) && File.Exists(theirs), "moved as a link, what it points at untouched");
			Directory.Delete(moved);
		} finally {
			File.SetAttributes(theirs, FileAttributes.Normal);
		}
		// a tool's own folder that is a link: its old content goes to the run's backup, the link stays for the tool to write into
		var toolLink = Path.Combine(f.Root, "tool out link");
		var toolReal = Path.Combine(f.Root, "tool out elsewhere");
		Directory.CreateDirectory(toolReal);
		File.WriteAllText(Path.Combine(toolReal, "old.txt"), "old");
		Check(Junction(toolLink, toolReal), "a third junction");
		var stays = false;
		var inBackup = 0;
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			StepKit.ParkDir(ctx, toolLink, "linked tool folder");
			stays = Files.IsLink(toolLink) && !File.Exists(Path.Combine(toolReal, "old.txt"));
			inBackup = Directory.GetFiles(Path.Combine(ctx.BackupFolder, "outputs"), "old.txt", SearchOption.AllDirectories).Length;
		}));
		f.WriteSetup(("synthesis", new JsonObject()));
		try {
			Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
			Check(stays, "the link stays where it is, emptied");
			Equal(1, inBackup, "its old content is in the backup");
		} finally {
			if (Directory.Exists(toolLink) && Files.IsLink(toolLink)) {
				Directory.Delete(toolLink);
			}
		}
	}

	// the small faults of the file work: a link inside what is moved, a mod list with two kinds of line ending, an output mod
	// half made, a game folder that is gone, a place that is no place
	static void T_TheSmallFaultsOfTheFileWork()
	{
		using var f = new FakeMo2();
		// a link inside a folder that is moved goes as the link it is: the folder it points at keeps what it holds
		var kept = Path.Combine(f.Root, "Kept elsewhere");
		var from = Path.Combine(f.Root, "Moved from");
		var into = Path.Combine(f.Root, "Moved into");
		Directory.CreateDirectory(kept);
		Directory.CreateDirectory(from);
		File.WriteAllText(Path.Combine(kept, "theirs.txt"), "x");
		File.WriteAllText(Path.Combine(from, "plain.txt"), "y");
		var link = Path.Combine(from, "link");
		Junction(link, kept);
		if (Directory.Exists(link)) {
			Check(Files.IsLink(link) && !Files.IsLink(kept), "a junction is a link, a plain folder is none");
			Files.MoveContents(from, into);
			Check(File.Exists(Path.Combine(into, "plain.txt")) && Files.IsLink(Path.Combine(into, "link")), "the file moved, and the link moved as a link");
			Check(File.Exists(Path.Combine(kept, "theirs.txt")), "the folder the link points at still holds its file");
			Directory.Delete(Path.Combine(into, "link"));
		} else {
			Console.WriteLine("      (no junction could be made here: the link part is skipped)");
		}

		// a mod list with both kinds of line ending is read line by line, and written back in the kind most of its lines have
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		var was = f.Text("modlist.txt");
		File.WriteAllText(list, "+Mixed One\n-Mixed Two\r\n" + was);
		Directory.CreateDirectory(Path.Combine(f.Mods, "Mixed One"));
		Directory.CreateDirectory(Path.Combine(f.Mods, "Mixed Two"));
		var p = ProfileOf(f);
		var states = p.States();
		Check(states.TryGetValue("Mixed One", out var one) && one == '+' && states.TryGetValue("Mixed Two", out var two) && two == '-', "each of the two mods is its own line: " + string.Join(", ", states.Keys.Take(4)));
		p.SetMods(["Mixed Two"], ["Mixed One"]);
		Check(f.Text("modlist.txt").StartsWith("-Mixed One\r\n+Mixed Two\r\n", StringComparison.Ordinal), "switched, and written in the list's own kind of line ending");
		File.WriteAllText(list, was);

		// an output mod whose line cannot be written (Mod Organizer is open) leaves no folder behind
		var open = new Profile(Mo2Instance.Load(f.Exe, f.Instance), "Test", () => true);
		Throws<Mo2OpenException>(() => open.EnsureMod("Never Listed"), "a line cannot be written while Mod Organizer is open");
		Check(!Directory.Exists(Path.Combine(f.Mods, "Never Listed")), "and the folder made for it is gone again");
		Directory.CreateDirectory(Path.Combine(f.Mods, "Was There"));
		File.WriteAllText(Path.Combine(f.Mods, "Was There", "theirs.esp"), "x");
		Throws<Mo2OpenException>(() => open.EnsureMod("Was There"), "the same for a folder that was there before");
		Check(File.Exists(Path.Combine(f.Mods, "Was There", "theirs.esp")), "which is never taken away");

		// a log read as it grows is the whole log at every look, whatever was added, and starts over on a file that was replaced
		var grownLog = Path.Combine(f.Root, "growing.log");
		var grown = new GrowingText();
		File.WriteAllText(grownLog, "[10:00] Loading Settings Complete\n[10:01] Shader compilation started\n");
		Equal(File.ReadAllText(grownLog), grown.Read(grownLog), "the first look: all of it");
		File.AppendAllText(grownLog, "[10:02] remaining=12 (ünïcode)\n[10:03] half a li");
		Equal(File.ReadAllText(grownLog), grown.Read(grownLog), "what was added, a line still being written with it");
		File.AppendAllText(grownLog, "ne\n");
		Equal(File.ReadAllText(grownLog), grown.Read(grownLog), "the line once it is whole, once");
		Equal(File.ReadAllText(grownLog), grown.Read(grownLog), "nothing added: the same text");
		// a look that falls in the middle of a letter: the letter is whole at the next look
		var letter = Encoding.UTF8.GetBytes("é");
		using (var cutFile = new FileStream(grownLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) {
			cutFile.Write(Encoding.UTF8.GetBytes("[10:04] caf"));
			cutFile.Write(letter, 0, 1);
			cutFile.Flush();
			Check(grown.Read(grownLog).Contains("[10:04] caf", StringComparison.Ordinal), "half a letter at the end of a look");
			cutFile.Write(letter, 1, 1);
			cutFile.Write("\n"u8);
		}
		Equal(File.ReadAllText(grownLog), grown.Read(grownLog), "the letter whole at the next look");
		File.WriteAllText(grownLog, "[11:00] another start's log, longer than the first one was, so its length alone would not tell that it is new\n[11:01] Shader compilation completed\n" + new string('x', 200) + "\n");
		Equal(File.ReadAllText(grownLog), grown.Read(grownLog), "a file that was replaced is read from its start");
		// the very first look ends inside a letter: that half settles nothing about how the file is written
		var cutLog = Path.Combine(f.Root, "cut at first.log");
		var cutLook = new GrowingText();
		File.WriteAllBytes(cutLog, [.. Encoding.UTF8.GetBytes("ready\ncaf"), letter[0]]);
		Check(cutLook.Read(cutLog).StartsWith("ready\ncaf", StringComparison.Ordinal), "the first look ends in half a letter");
		File.AppendAllBytes(cutLog, [letter[1], (byte)'\n']);
		Equal("ready\ncafé\n", cutLook.Read(cutLog), "the letter reads right once it is whole");
		File.AppendAllText(cutLog, "naïve\n");
		Equal("ready\ncafé\nnaïve\n", cutLook.Read(cutLog), "and so do the lines after it");
		// plain letters first, then a line in the PC's own code page: the whole file is read as that
		var ownLog = Path.Combine(f.Root, "own code page.log");
		var ownLook = new GrowingText();
		byte[] ownLine = [.. "caf"u8, 0xE9, (byte)'\n'];
		File.WriteAllText(ownLog, "plain\n");
		Equal("plain\n", ownLook.Read(ownLog), "plain letters read either way");
		File.AppendAllBytes(ownLog, ownLine);
		Equal(Files.DecodeText([.. "plain\n"u8, .. ownLine]), ownLook.Read(ownLog), "a later line that is no UTF-8 is read in the PC's own code page");

		// no place is under nothing and the same as nothing
		Check(!Files.SamePath("", f.Root) && !Files.SamePath(f.Root, " ") && !Files.IsUnder("", f.Root) && !Files.IsUnder(f.Root, ""), "an empty text names no folder");
		Equal<long?>(null, Files.FreeOn(""), "and has no free room to tell");

		// a game folder that is gone is said by the check, with what to do
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		Directory.Move(f.Game, f.Game + " moved");
		var found = Preflight.Run(setup, Mo2Instance.Load(f.Exe, f.Instance), ["landscapefixer"]);
		Check(found.FirstOrDefault(i => i.Blocking && i.Text.Contains("the game folder Mod Organizer names is not there", StringComparison.Ordinal)) is { } gone &&
			RunnerErrors.Find(gone.Text)?.Code == "TR-214", "the check stops on it: " + string.Join(" | ", found.Where(i => i.Blocking).Select(i => i.Text)));
		Directory.Move(f.Game + " moved", f.Game);
	}

	// a setup brought in from elsewhere starts no program of its own choosing and allows no add-on for the player
	static void T_AnImportedSetupStartsNoProgramOfItsOwn()
	{
		var s = Setup.FromJson(new JsonObject {
			["mo2"] = new JsonObject { ["exe"] = @"C:\Elsewhere\ModOrganizer.exe", ["profile"] = "Test" },
			[AddOns.NoAskSetting] = true,
			["steps"] = new JsonArray(
				new JsonObject { ["step"] = "grass", ["settings"] = new JsonObject { ["fasterngio_exe"] = @"C:\Elsewhere\FasterNGIO.exe", ["fasterngio_args"] = "--world all", ["output_mod"] = "Grass" } },
				new JsonObject { ["step"] = "acmos", ["settings"] = new JsonObject { ["exe"] = @"C:\Elsewhere\acmos.exe", ["output_mod"] = "Roads" } },
				new JsonObject { ["step"] = "treelodgen", ["settings"] = new JsonObject { ["launcher"] = @"C:\Elsewhere\run.cmd", ["output_mod"] = "Trees" } },
				new JsonObject { ["step"] = AddOns.KeyPrefix + "Their Patcher", ["settings"] = new JsonObject { ["sha"] = "ABCD", ["output_mod"] = "Theirs" } }),
		});
		var gone = s.TakeOutWhatStartsPrograms();
		Equal("", s.Mo2Exe, "Mod Organizer's program is the player's to set");
		Check(s.Root[AddOns.NoAskSetting] == null, "the add-on question is not switched off for them");
		var steps = s.Steps;
		Check(steps.All(st => Setup.Str(st.Settings, "exe") == null && Setup.Str(st.Settings, "fasterngio_exe") == null && Setup.Str(st.Settings, "launcher") == null), "no step starts a program it names");
		Check(steps.Single(st => st.Key.StartsWith(AddOns.KeyPrefix, StringComparison.Ordinal)).Settings["sha"] == null, "the add-on is asked about again");
		Check(steps.All(st => Setup.Str(st.Settings, "output_mod") != null) && Setup.Str(steps[0].Settings, "fasterngio_args") == "--world all", "what is no program stays");
		Equal(6, gone.Count, "each named: " + string.Join(" | ", gone));
		// and every folder or file of the PC it came from: a step empties its output folder into the run's backup, and on this PC
		// that path could be any folder of the player's
		var paths = Setup.FromJson(new JsonObject {
			["backups"] = @"D:\Their\Backups",
			["steps"] = new JsonArray(new JsonObject { ["step"] = "pandora", ["settings"] = new JsonObject { ["output_dir"] = @"C:\Users\them\Documents", ["output_mod"] = "Behaviours" } }),
		});
		var taken = SetupShare.TakeOutPaths(paths.Root);
		Check(paths.Root["backups"] == null && Setup.Str(paths.Steps[0].Settings, "output_dir") == null && Setup.Str(paths.Steps[0].Settings, "output_mod") == "Behaviours",
			"its folders are taken out, its mod names stay");
		Check(taken.Count == 2 && taken.All(t => t.Contains("the PC it came from", StringComparison.Ordinal)), "each named: " + string.Join(" | ", taken));
	}

	// a whole number a setup gives is a number the program can hold, or it is not given
	static void T_ASetupNumberOutOfRangeIsNoNumber()
	{
		var o = new JsonObject { ["big"] = 1e12, ["small"] = -1e12, ["plain"] = 5.0, ["whole"] = 7 };
		Equal<int?>(null, Setup.Int(o, "big"), "too big");
		Equal<int?>(null, Setup.Int(o, "small"), "too small");
		Equal<int?>(5, Setup.Int(o, "plain"), "a whole number written as 5.0");
		Equal<int?>(7, Setup.Int(o, "whole"), "a whole number");
	}

	// Mod Organizer's own entries (the game's, a Creation Club file's) are in the list and are never switched: said so
	static void T_ModOrganizersOwnEntriesAreNeverSwitched()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		try {
			p.SetMods(["DLC: Dawnguard"], []);
			Check(false, "it was switched");
		} catch (KeyNotFoundException e) {
			Check(e.Message.Contains("Mod Organizer's own entries", StringComparison.Ordinal), "said as what it is, not as missing: " + e.Message);
		}
		Check(p.States()["DLC: Dawnguard"] == '*', "left as it was");
	}

	// Every setting a step reads off its own settings is one the Settings window can show: written by a new setup, or offered
	// with its default when the step's file does not hold it. Read off the source, so a setting added to a step without its
	// row in the window fails here
	static void T_EverySettingAStepReadsIsOffered()
	{
		var steps = Path.Combine(SourceDir(), "Steps");
		if (!Directory.Exists(steps)) {
			Console.WriteLine($"  (skipped: the source is not at {steps})");
			return;
		}
		var reads = new System.Text.RegularExpressions.Regex(@"\ba_s\.(?:Str|Req|Int|Num|Bool|List)\(""(\w+)""");
		var classAt = new System.Text.RegularExpressions.Regex(@"(?m)^public (?:sealed |abstract |static |partial )*class (\w+)(?: : (\w+))?");
		var keyAt = new System.Text.RegularExpressions.Regex(@"string Key => ""(\w+)""");
		// what a new setup writes for the step, or the step cannot run without (the check names it)
		string[] basics = ["executable", "output_mod", "then_move_to_mod", "output_dir", "launch_executable", "exe", "by", "fasterngio_exe", "reads", "dyndolod", "worldspaces",
			"interior_cell", "wait_s", "dyndolod_wait_s", "dyndolod_off", "save", "shader_mod", "settings_mod", "tool_writes", "file", "sha", "image", "replaces"];
		HashSet<string> KeysIn(string a_text) => [.. reads.Matches(a_text).Select(m => m.Groups[1].Value)];
		var keysOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		var baseOf = new Dictionary<string, string>(StringComparer.Ordinal);
		var stepOf = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var file in Directory.GetFiles(steps, "*.cs")) {
			var text = File.ReadAllText(file);
			var marks = classAt.Matches(text);
			for (var i = 0; i < marks.Count; i++) {
				var body = text[marks[i].Index..(i + 1 < marks.Count ? marks[i + 1].Index : text.Length)];
				var name = marks[i].Groups[1].Value;
				keysOf[name] = KeysIn(body);
				if (marks[i].Groups[2].Success) {
					baseOf[name] = marks[i].Groups[2].Value;
				}
				if (keyAt.Match(body) is { Success: true } k) {
					stepOf[name] = k.Groups[1].Value;
				}
			}
		}
		Check(stepOf.Count >= 13, $"the steps were found in the source ({stepOf.Count}): {string.Join(", ", stepOf.Values)}");
		foreach (var (name, step) in stepOf) {
			var read = new HashSet<string>(keysOf[name], StringComparer.Ordinal);
			if (baseOf.TryGetValue(name, out var parent) && keysOf.TryGetValue(parent, out var theirs)) {
				read.UnionWith(theirs);
			}
			var offered = ToolRunner.App.SettingsPanel.OfferedKeys(step);
			var hidden = read.Where(k => !offered.Contains(k) && !basics.Contains(k)).Order(StringComparer.Ordinal).ToList();
			Check(hidden.Count == 0, $"{step} reads {hidden.Count} setting(s) its page never offers: {string.Join(", ", hidden)}");
		}
		// What the game steps share: a game start that loads a save (InGame). Each save step is held to what IT reads - the
		// InGame functions its own class calls, and the ones those call - on its own page: a setting both read is on both pages
		var inGameText = File.ReadAllText(Path.Combine(SourceDir(), "InGame.cs"));
		var methodAt = new System.Text.RegularExpressions.Regex(@"(?m)^\t(?:public |internal |private )*static [^\n=;]*?\b(\w+)(?:<[^>\n]*>)?\(");
		var methods = methodAt.Matches(inGameText);
		var bodyOf = new Dictionary<string, string>(StringComparer.Ordinal);
		for (var i = 0; i < methods.Count; i++) {
			var body = inGameText[methods[i].Index..(i + 1 < methods.Count ? methods[i + 1].Index : inGameText.Length)];
			bodyOf[methods[i].Groups[1].Value] = bodyOf.GetValueOrDefault(methods[i].Groups[1].Value, "") + body;
		}
		var callAt = new System.Text.RegularExpressions.Regex(@"\b(\w+)\(");
		HashSet<string> ReadThrough(IEnumerable<string> a_calls)
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var todo = new Stack<string>(a_calls.Where(bodyOf.ContainsKey));
			var keys = new HashSet<string>(StringComparer.Ordinal);
			while (todo.Count > 0) {
				var m = todo.Pop();
				if (!seen.Add(m)) {
					continue;
				}
				keys.UnionWith(KeysIn(bodyOf[m]));
				foreach (var c in callAt.Matches(bodyOf[m]).Select(x => x.Groups[1].Value).Where(bodyOf.ContainsKey)) {
					todo.Push(c);
				}
			}
			return keys;
		}
		var gameSteps = File.ReadAllText(Path.Combine(steps, "GameSteps.cs"));
		var stepClasses = classAt.Matches(gameSteps);
		var inGameAll = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (cls, step) in new[] { ("CleanSaveStep", "cleansave"), ("PlaySaveStep", "playsave") }) {
			var at = stepClasses.FirstOrDefault(m => m.Groups[1].Value == cls);
			Check(at != null, $"{cls} was found in the source");
			if (at == null) {
				continue;
			}
			var next = stepClasses.FirstOrDefault(m => m.Index > at.Index)?.Index ?? gameSteps.Length;
			var calls = new System.Text.RegularExpressions.Regex(@"\bInGame\.(\w+)\(").Matches(gameSteps[at.Index..next]).Select(x => x.Groups[1].Value);
			var stepReads = ReadThrough(calls);
			inGameAll.UnionWith(stepReads);
			var offered = ToolRunner.App.SettingsPanel.OfferedKeys(step);
			var lost = stepReads.Where(k => !offered.Contains(k) && !basics.Contains(k)).Order(StringComparer.Ordinal).ToList();
			Check(lost.Count == 0, $"{step} reads, through the game's own steps, settings its page never offers: {string.Join(", ", lost)}");
		}
		Check(inGameAll.Count >= 6, $"the save steps' settings were found in the source ({inGameAll.Count})");
		var offer = KeysIn(File.ReadAllText(Path.Combine(SourceDir(), "GameMods.cs")));
		Check(offer.Contains("max_offers") && offer.Contains("offer_wait_min"), "Faster Archives' two settings were found in the source");
		foreach (var step in (string[])["cleansave", "playsave", "shaders", "grass"]) {
			var offered = ToolRunner.App.SettingsPanel.OfferedKeys(step);
			Check(offer.All(offered.Contains), $"{step} offers what Faster Archives' wait reads");
		}
		// each has words of its own, never the setting's raw name
		foreach (var step in StepCatalog.DefaultOrder) {
			var bare = ToolRunner.App.SettingsPanel.OfferedKeys(step).Where(k => !ToolRunner.App.SettingsPanel.StepLabels.Contains(ToolRunner.App.SettingsPanel.LabelOf(k, step))).ToList();
			Check(bare.Count == 0, $"{step}: no label for {string.Join(", ", bare)}");
		}
		// the two save steps have no time limit of their own, so none is offered; every other step's is
		Check(!ToolRunner.App.SettingsPanel.OfferedKeys("cleansave").Contains("deadline_min") && !ToolRunner.App.SettingsPanel.OfferedKeys("playsave").Contains("deadline_min") &&
		      ToolRunner.App.SettingsPanel.OfferedKeys("grass").Contains("deadline_min"), "a time limit is offered where one is read");
		Equal("Wait after the teleport (seconds)", ToolRunner.App.SettingsPanel.LabelOf("settle_s", "playsave"), "the save steps' wait is named for what it is");
		Equal("Wait before closing (seconds)", ToolRunner.App.SettingsPanel.LabelOf("settle_s", "shaders"), "the shader step's too");
		Check(ToolRunner.App.SettingsPanel.OfferedKeys("playsave").Contains("save") && ToolRunner.App.SettingsPanel.LabelOf("save", "playsave") != ToolRunner.App.SettingsPanel.LabelOf("save", "cleansave"),
			"the final save's own save is offered, under words of its own");
	}

	static void T_EveryRefusalIsInTheCatalog()
	{
		Equal(RunnerErrors.All.Count, RunnerErrors.All.Select(e => e.Code).Distinct().Count(), "one code per entry");
		Check(RunnerErrors.All.All(e => System.Text.RegularExpressions.Regex.IsMatch(e.Code, @"^TR-\d{3}$") && e.What.Length > 20 && e.Todo.Length > 20 && e.Any.All(a => a.Trim().Length >= 6)),
			"each entry has its code, says what it means and what to do, and is found by real words");
		// what the engine words elsewhere and hands on as a refusal: the plan, Mod Organizer, the setup's own problems, the last run's record
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("cleansave", []), ("dyndolod", new JsonObject { ["output_mod"] = "Output B" }));
		var said = new List<string>();
		void Thrown(Action a_act)
		{
			try {
				a_act();
			} catch (Exception e) when (e is ArgumentException or IOException) {
				said.Add(e.Message);
			}
		}
		Thrown(() => Runner.Plan(setup, "nosuchstep", null, null));
		Thrown(() => Runner.Plan(setup, null, "dyndolod", "landscapefixer"));
		setup.Root["newGame"] = true;
		Thrown(() => Runner.Plan(setup, "cleansave", null, null));
		Thrown(() => Mo2Instance.Load(Path.Combine(f.Root, "nowhere", "ModOrganizer.exe"), null));
		Equal(4, said.Count, "the four were refused: " + string.Join(" | ", said));
		said.AddRange(Setup.FromJson(new JsonObject()).Problems());
		said.AddRange(Setup.FromJson(new JsonObject { ["mo2"] = new JsonObject { ["exe"] = "x", ["profile"] = "p" }, ["backups"] = "relative folder", ["newGame"] = true,
			["steps"] = new JsonArray(new JsonObject { ["step"] = "cleansave" }, new JsonObject { ["step"] = "cleansave" }, new JsonObject { ["step"] = "snowfixer" },
				new JsonObject { ["step"] = "nosuchstep", ["settings"] = new JsonObject { ["output_mod"] = @"..\outside" } }) }).Problems());
		said.AddRange(Setup.FromJson(new JsonObject { ["steps"] = new JsonArray(new JsonObject { ["step"] = "synthesis", ["on"] = false }) }).Problems());
		// a plugin place without both its names is refused at the check, and never read as a rule (it threw at the put-back's end)
		var badPlace = Setup.FromJson(new JsonObject { ["placement"] = JsonNode.Parse("[[null, \"Skyrim.esm\"], [\"Only.esp\"], [\"\", \"Skyrim.esm\"], [\"Mine.esp\", \"  \"], [1, \"Skyrim.esm\"], [\"Mine.esp\", true], [\"Mine.esp\", \"Skyrim.esm\"]]") });
		Check(badPlace.Placement.Count == 1 && badPlace.Placement[0].ToString().Contains("Mine.esp", StringComparison.Ordinal), "only the whole entry is a plugin place");
		said.AddRange(badPlace.Problems().Where(p => p.Contains("placement", StringComparison.Ordinal)));
		Check(said[^1].Contains("placement", StringComparison.Ordinal), "and the check names the others");
		Check(said.Count >= 15, "the setup's own problems were all met: " + string.Join(" | ", said));
		// a blocked start, as a run says it
		var notes = Path.Combine(Runner.RunsDir(f.Data), "2026-01-01 00-00-00 Test");
		Directory.CreateDirectory(notes);
		File.WriteAllText(Path.Combine(notes, "journal.json"), "{ not a record");
		said.Add(Runner.CheckStart(new RunRequest(f.SetupFile, f.Data)).Why);
		Check(said[^1].Contains("is damaged", StringComparison.Ordinal), "a damaged record of the last run refuses a start: " + said[^1]);
		var none = said.Where(s => RunnerErrors.Find(s) == null).ToList();
		Check(none.Count == 0, "no catalog entry for: " + string.Join(" | ", none));

		var core = SourceDir();
		if (!Directory.Exists(core)) {
			Console.WriteLine($"  (the source scan skipped: the source is not at {core})");
			return;
		}
		var missing = RefusalsNotInTheCatalog(core, out var seen);
		Check(seen >= 60, $"the scan found the engine's refusals ({seen})");
		Check(missing.Count == 0, $"{missing.Count} refusal(s) with no catalog entry (RunnerErrors.cs) - " + string.Join(" || ", missing.Take(6)));
	}

	// a step's numbers come out of a file anyone may have written: one below zero is the step's own default, minutes are one at least
	static void T_AStepsNumbersAreHeldToWhatTheWindowAllows()
	{
		var s = new StepSettings(new JsonObject { ["start_min"] = -5, ["wait_s"] = -1, ["quiet_min"] = 0, ["idle_s"] = 0, ["crash_wait_min"] = -2.5, ["offer_wait_min"] = 0.02,
			["min_files"] = 0, ["max_restarts"] = 7, ["build_min"] = 2.9 });
		Equal(8, s.Int("start_min", 8), "minutes below zero: the default");
		Equal(60, s.Int("wait_s", 60), "seconds below zero: the default");
		Equal(1, s.Int("quiet_min", 5), "no minutes at all: one minute");
		Equal(0, s.Int("idle_s", 45), "no seconds is a number");
		Equal(0, s.Int("min_files", 100), "and so is no file");
		Equal(7, s.Int("max_restarts", 3), "a number as it stands");
		Equal(2, s.Int("build_min", 15), "a number with a fraction, where a whole one is read: its whole part");
		Equal(30.0, s.Num("crash_wait_min", 30), "a wait below zero: the default");
		Equal(0.02, s.Num("offer_wait_min", 30), "a part of a minute stands");
		Equal(15, s.Int("not there", 15), "what the file does not hold: the default");
		Equal(TimeSpan.FromMinutes(1), new StepSettings(new JsonObject { ["deadline_min"] = 0 }).Deadline(60), "a time limit of none is one minute");
		Equal(TimeSpan.FromMinutes(60), new StepSettings(new JsonObject { ["deadline_min"] = -3 }).Deadline(60), "one below zero is the step's own");
		// what Settings writes into the setup it shows: a whole number as a long, any other as a double
		var typed = new JsonObject { ["keepBackups"] = JsonValue.Create(20L), ["minFreeGb"] = JsonValue.Create(35L), ["guidedReminderMin"] = JsonValue.Create(2.5), ["idleMinutes"] = 4,
			["name"] = "a word", ["far"] = JsonValue.Create(5_000_000_000L) };
		Equal(20, Setup.Int(typed, "keepBackups"), "a whole number typed in Settings is that number before the setup is loaded again");
		Equal(35.0, Setup.Num(typed, "minFreeGb"), "... asked for either way");
		Equal(2.5, Setup.Num(typed, "guidedReminderMin"), "a number with a fraction");
		Equal(2, Setup.Int(typed, "guidedReminderMin"), "... and its whole part");
		Equal(4, Setup.Int(typed, "idleMinutes"), "a number the code put there");
		Equal(4.0, Setup.Num(typed, "idleMinutes"), "... asked for as one with a fraction");
		Check(Setup.Int(typed, "name") == null && Setup.Num(typed, "name") == null && Setup.Int(typed, "none") == null, "a word, and nothing, are no number");
		Check(Setup.Int(typed, "far") == null && Setup.Num(typed, "far") == 5_000_000_000d, "a number past what a whole one holds is none where a whole one is asked for");
		var read = Setup.RootOf("""{ "keepBackups": 20, "guidedReminderMin": 2.5, "name": "seven" }""")!;
		Check(Setup.Int(read, "keepBackups") == 20 && Setup.Num(read, "keepBackups") == 20d && Setup.Num(read, "guidedReminderMin") == 2.5 && Setup.Int(read, "name") == null,
			"read from a file, the same answers");
		Equal(20, new StepSettings(new JsonObject { ["min_files"] = JsonValue.Create(20L) }).Int("min_files", 100), "a step's number typed in Settings too");
		// written by hand: quotes round a number or a switch, one name where a list is read, a number past every time
		var hand = Setup.RootOf("""
			{ "deadline_min": "90", "wait_s": " 45 ", "crash_wait_min": "2.5", "on": "false", "newGame": "True", "replaces": "Old Output", "enable_during": ["A", "B", ""],
			  "word": "ninety", "comma": "1,5", "nan": "NaN", "far": "1e400", "huge": 1e300, "list": 7, "empty": "  ", "yes": "yes" }
			""")!;
		Check(Setup.Int(hand, "deadline_min") == 90 && Setup.Int(hand, "wait_s") == 45 && Setup.Num(hand, "crash_wait_min") == 2.5, "a number in quotes is that number");
		Check(Setup.Bool(hand, "on") == false && Setup.Bool(hand, "newGame") == true, "a switch in quotes is that switch");
		Check(Setup.Bool(hand, "yes") == null && Setup.Bool(hand, "deadline_min") == null && Setup.Bool(hand, "none") == null, "any other word is no switch");
		Check(Setup.Num(hand, "word") == null && Setup.Num(hand, "comma") == null && Setup.Num(hand, "nan") == null && Setup.Num(hand, "far") == null && Setup.Num(hand, "empty") == null,
			"a word, a comma, and what is no number are none");
		Equal("Old Output", string.Join("|", Setup.List(hand, "replaces")), "one name where a list is read is a list of it");
		Equal("A|B", string.Join("|", Setup.List(hand, "enable_during")), "a list as before");
		Check(Setup.List(hand, "list").Count == 0 && Setup.List(hand, "empty").Count == 0 && Setup.List(hand, "none").Count == 0, "a number, blanks and nothing are no list");
		Equal(30.0, new StepSettings(hand).Num("huge", 30), "a wait past every time: the default");
		Equal(TimeSpan.FromMinutes(90), new StepSettings(hand).Deadline(60), "the time limit in quotes is the step's");
		var byHand = Setup.FromJson(new JsonObject { ["beforeRun"] = "start.bat", ["steps"] = new JsonArray(new JsonObject { ["step"] = "texgen", ["on"] = "false" }) });
		Check(!byHand.Steps[0].On, "a step switched \"false\" is off");
		Equal(1, SetupShare.Share(byHand, "1.0", []).Removed.Count(r => r.Contains("beforeRun", StringComparison.Ordinal)), "one command given as a word is named when the setup is shared");
		Equal(1, byHand.TakeOutCommands(), "... and taken out of a setup that is brought in");
		Check(Setup.Num(Setup.RootOf("""{ "far": 1e400 }""")!, "far") == null, "a number past what a number holds is none");
		bool Due(double a_every) => ToolRunner.App.MainViewModel.ReminderDue(a_every, new DateTime(2026, 1, 1), new DateTime(2026, 1, 1, 0, 5, 0));
		Check(!Due(1e300) && !Due(double.NaN) && !Due(0) && !Due(6) && Due(5) && Due(1), "a reminder every 1e300 minutes is never due, and no error");
	}

	// a shader log is asked by what it says, kept as it grows: the answer is the whole log's, and the log itself is never kept
	static void T_AShaderLogIsKeptAsWhatItSays()
	{
		static ShaderStep.Look Of(string a_text, int a_files = 500) => new(a_text, a_files, 400, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40), 0.2, 6.0, TimeSpan.FromSeconds(40), true);
		var noise = string.Concat(Enumerable.Range(0, 3000).Select(i => $"[12:00:{i % 60:00}.000] [info] compiled shader {i} in 12 ms\n"));
		var timing = string.Concat(Enumerable.Range(0, 500).Select(i => $"[12:01:{i % 60:00}.000] [info] [ShaderTiming] slow shader {i}: 900 ms, remaining={500 - i}\n"));
		// Open Shaders: its own lines open and close a compile
		var open = "Open Shaders 2.20.0\nLoading Settings Complete\n" + noise + "[12:00:05] Shader compilation started\n" + noise;
		var closed = open + "[12:20:00] Shader compilation completed in 1200 s\n";
		// Bottled Shaders: a count of what is left
		var counting = "Loaded theme\n" + noise + timing;
		var counted = counting + "[12:30:00.000] [info] [ShaderTiming] slow shader: 900 ms, remaining=0\n";
		foreach (var (name, log) in new[] { ("a compile still open", open), ("a compile that closed", closed), ("a count still above none", counting), ("a count at none", counted),
			         ("a shader mod that stood down", "Incompatible DLL found: Remove it to use this mod\n" + noise), ("a log that says nothing", noise) }) {
			// fed as it grows: in pieces that end anywhere, the last line not yet whole at some looks
			var digest = new ShaderStep.LogDigest();
			var whole = "";
			for (var at = 0; at < log.Length; at += 7919) {
				var piece = log[at..Math.Min(log.Length, at + 7919)];
				var before = whole.Length - (whole.LastIndexOf('\n') + 1);
				whole += piece;
				// (the lines this piece made whole: from the end of the last whole line before it to the last line end now)
				var from = whole.Length - piece.Length - before;
				var end = whole.LastIndexOf('\n') + 1;
				if (end > from) {
					digest.Take(whole[from..end]);
				}
				var text = digest.TextWith(whole[end..]);
				Equal(ShaderStep.Done(Of(whole), 50, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30)), ShaderStep.Done(Of(text), 50, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30)),
					$"{name}, {whole.Length} letters in: is the compile over - asked of what is kept as of the whole log");
				Equal(InGame.RemainingIn(whole), InGame.RemainingIn(text), $"{name}: the last count of what is left");
			}
			var kept = digest.Text;
			Check(kept.Length < 60_000 && kept.Length <= log.Length, $"{name}: what is kept stays small ({kept.Length} letters of {log.Length})");
			Equal(System.Text.RegularExpressions.Regex.IsMatch(log, "Loading Settings Complete|Loaded theme|Shader compilation started|ShaderTiming"),
				System.Text.RegularExpressions.Regex.IsMatch(kept, "Loading Settings Complete|Loaded theme|Shader compilation started|ShaderTiming"), $"{name}: whether the shader mod shows it runs");
			Equal(System.Text.RegularExpressions.Regex.Match(log, @"^.*(Incompatible DLL|Remove it to use|disabled itself|will not run).*$", System.Text.RegularExpressions.RegexOptions.Multiline).Value,
				System.Text.RegularExpressions.Regex.Match(kept, @"^.*(Incompatible DLL|Remove it to use|disabled itself|will not run).*$", System.Text.RegularExpressions.RegexOptions.Multiline).Value,
				$"{name}: the line it stood down with");
			digest.Restart();
			Equal("", digest.Text, "a log that began anew is asked anew");
		}
		// the reader that feeds it: only what a look found new, the log never kept
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "digest " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		try {
			var file = Path.Combine(dir, "shader.log");
			File.WriteAllText(file, "first line\nsecond li");
			var reader = new GrowingText { KeepAll = false };
			Equal("first line\n", reader.Read(file), "the first look: every whole line");
			Check(reader.Restarted && reader.Added == "first line\n" && reader.Unfinished == "second li", "... told as a new beginning, with the line still being written apart");
			File.AppendAllText(file, "ne\nthird\n");
			Equal("second line\nthird\n", reader.Read(file), "the next look: only what it found new");
			Check(!reader.Restarted && reader.Unfinished == "", "... and no new beginning");
			Equal("", reader.Read(file), "a look that finds nothing new gives nothing");
			File.WriteAllText(file, "another log\n");
			Equal("another log\n", reader.Read(file), "a file that was replaced is read from its start");
			Check(reader.Restarted, "... and told as a new beginning");
			// files counted, summed and dated off the walk itself
			File.WriteAllBytes(Path.Combine(dir, "five.bin"), new byte[5]);
			Directory.CreateDirectory(Path.Combine(dir, "sub"));
			File.WriteAllBytes(Path.Combine(dir, "sub", "seven.bin"), new byte[7]);
			Equal(3, Files.CountFiles(dir), "every file under a folder is counted");
			Equal(Files.Under(dir).Sum(f => f.Length), Files.SizeOf(dir), "their sizes add up to what the files themselves say");
			Equal(Files.Under(dir).Max(f => f.LastWriteTimeUtc), Files.NewestWrite(dir), "and the newest of them is found");
			Equal(0, Files.CountFiles(Path.Combine(dir, "not there")), "a folder that is not there holds none");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// a run's record holds what the run wrote, never what is worked out from it at the moment it is asked: who is alive is
	// a question to Windows, and a record that stored the answer stored it out of date
	static void T_ARecordHoldsWhatWasWrittenNotWhatIsWorkedOut()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "record " + Guid.NewGuid().ToString("N")[..8], "2026-11-01 10-00-00 worked out");
		Directory.CreateDirectory(dir);
		try {
			var j = new Journal { Id = Path.GetFileName(dir), Folder = dir, SetupName = "worked out", Started = DateTime.Now, State = RunState.Running, Pid = Environment.ProcessId,
				Steps = [new StepRecord { Key = "texgen", State = StepState.Running }] };
			j.Save();
			var text = File.ReadAllText(j.File);
			Check(!text.Contains("OwnerAlive", StringComparison.Ordinal) && !text.Contains("Unfinished", StringComparison.Ordinal) && !text.Contains("\"Live\"", StringComparison.Ordinal),
				"none of the three worked-out answers is in the file");
			Check(text.Contains("\"State\": \"Running\"", StringComparison.Ordinal) && text.Contains("\"Pid\"", StringComparison.Ordinal), "what they are worked out from is");
			var read = Journal.Load(dir);
			Check(read.Unfinished && read.Live, "read back, a run that is going is unfinished and live");
			// a record an older version wrote still names them - with answers of the moment it was saved: they are passed over
			// (the run's own state, at the record's top level - a step's state further in stays as it is)
			File.WriteAllText(j.File, text.Replace("\n  \"State\": \"Running\"", "\n  \"State\": \"Finished\", \"Unfinished\": true, \"OwnerAlive\": true", StringComparison.Ordinal).
				Replace("\"Pid\": " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "\"Pid\": 0", StringComparison.Ordinal));
			var old = Journal.Load(dir);
			Check(old.State == RunState.Finished && !old.Unfinished && !old.OwnerAlive, "an older record's stored answers are not believed: the run finished, and nobody owns it");
		} finally {
			Directory.Delete(Path.GetDirectoryName(dir)!, true);
		}
	}

	// the window's look: every run folder is asked about only when something says a record changed - where the folder is watched
	static void T_ALookDoesNotWalkEveryRunFolderWhileNothingChanged()
	{
		using var f = new FakeMo2();
		var runs = Runner.RunsDir(f.Data);
		Journal Record(string a_id, RunState a_state)
		{
			var j = new Journal { Id = a_id, Folder = Path.Combine(runs, a_id), SetupFile = f.SetupFile, SetupName = "walk", Started = DateTime.Now, State = a_state,
				Steps = [new StepRecord { Key = "texgen", State = StepState.Done }] };
			Directory.CreateDirectory(j.Folder);
			j.Save();
			return j;
		}
		var first = Record("2026-11-01 10-00-00 walk", RunState.Finished);
		Record("2026-11-02 10-00-00 walk", RunState.Finished);
		var every = JournalCache.WholeEvery;
		try {
			// a command: nothing watches, every read is whole
			var plain = new JournalCache(runs);
			Equal(2, plain.Read().Count, "both records");
			_ = plain.Read();
			Equal(2, plain.WholeReads, "with no watcher every read looks at every run folder, as it always did");
			// the window: watched
			var cache = new JournalCache(runs) { Watched = true };
			Equal(2, cache.Read().Count, "both records, read whole the first time");
			for (var i = 0; i < 5; ++i) {
				Equal(2, cache.Read().Count, "... and handed out again");
			}
			Equal(1, cache.WholeReads, "while nothing changed the run folders are not looked at again");
			var handed = cache.Read();
			handed.Clear();
			Equal(2, cache.Read().Count, "what a caller does to the list it was handed is its own");
			// this process writes a record (an undo, a put-back): seen at once, without the watcher's word
			first.Undone = true;
			first.Save();
			Check(cache.Read().First(j => j.Id == first.Id).Undone && cache.WholeReads == 2, "a record this process wrote is read at the very next look");
			// a run folder comes: the runs folder's own time says so
			var third = Record("2026-11-03 10-00-00 walk", RunState.Finished);
			Equal(3, cache.Read().Count, "a new run is there at the next look");
			var wholeNow = cache.WholeReads;
			_ = cache.Read();
			Equal(wholeNow, cache.WholeReads, "... and then nothing is looked at again");
			// another program changed a record: the watcher says so
			cache.Touch();
			_ = cache.Read();
			Equal(wholeNow + 1, cache.WholeReads, "the watcher's word makes the next look a whole one");
			// a run folder goes
			Directory.Delete(third.Folder, true);
			Equal(2, cache.Read().Count, "a run that is gone is gone at the next look");
			// the trusted time over: looked at whole again, watcher or not
			JournalCache.WholeEvery = TimeSpan.Zero;
			wholeNow = cache.WholeReads;
			Thread.Sleep(20);
			_ = cache.Read();
			Equal(wholeNow + 1, cache.WholeReads, "past the trusted time every run folder is looked at again");
		} finally {
			JournalCache.WholeEvery = every;
		}
		// the look's pace
		Equal(TimeSpan.FromSeconds(1), ToolRunner.App.MainWindow.Pace(true, false, true, false), "something going: every second, wherever the window is");
		Equal(TimeSpan.FromSeconds(3), ToolRunner.App.MainWindow.Pace(false, false, false, true), "nothing going, the program in front");
		Equal(TimeSpan.FromSeconds(10), ToolRunner.App.MainWindow.Pace(false, false, false, false), "another program in front");
		Equal(TimeSpan.FromSeconds(20), ToolRunner.App.MainWindow.Pace(false, true, false, true), "minimised");
		Equal(TimeSpan.FromSeconds(20), ToolRunner.App.MainWindow.Pace(false, false, true, true), "the player's own game running");
	}

	// two tools set to write into one output mod: refused before anything is switched - but the terrain LOD and its roads are one output
	static void T_TwoToolsWithOneOutputModAreRefused()
	{
		static JsonObject Step(string a_key, string a_setting, string a_mod, bool a_on = true) =>
			new() { ["step"] = a_key, ["on"] = a_on, ["settings"] = new JsonObject { [a_setting] = a_mod } };
		var one = Setup.FromJson(new JsonObject { ["steps"] = new JsonArray(Step("synthesis", "output_mod", "My Outputs"), Step("xlodgen", "output_mod", "Terrain"),
			Step("acmos", "then_move_to_mod", "terrain"), Step("texgen", "output_mod", "my outputs"), Step("dyndolod", "output_mod", "DynDOLOD Output"), Step("pandora", "output_mod", "My Outputs", false)) });
		var all = one.EnabledSteps.Select(s => s.Key).ToList();
		var found = Preflight.SharedOutputs(one, all).ToList();
		Equal(1, found.Count, "one pair shares a mod; the terrain LOD and its roads are one output, and a step that is off writes nothing: " + string.Join(" | ", found.Select(i => i.Text)));
		Check(found[0].Blocking && found[0].Text.StartsWith(StepCatalog.Name("synthesis") + " and " + StepCatalog.Name("texgen") + " have the same output mod, \"My Outputs\"", StringComparison.Ordinal),
			"both are named, with the mod: " + found[0].Text);
		Equal("TR-324", RunnerErrors.Find(found[0].Text)?.Code, "... and it comes with what to do");
		Equal(0, Preflight.SharedOutputs(one, ["texgen", "dyndolod"]).Count(), "a run of only one of the two is no clash");
		// a shader cache's own output mod counts too: two caches set to one mod, or a cache set to another step's output
		static JsonObject Caches(params string[] a_outputs) => new() { ["step"] = "shaders", ["on"] = true, ["settings"] = new JsonObject {
			["caches"] = new JsonArray([.. a_outputs.Select(o => (JsonNode)new JsonObject { ["shader_mod"] = "Shaders " + o, ["output_mod"] = o })]) } };
		var twoCaches = Setup.FromJson(new JsonObject { ["steps"] = new JsonArray(Caches("Shader Cache", "shader cache")) });
		var cacheFound = Preflight.SharedOutputs(twoCaches, ["shaders"]).ToList();
		Check(cacheFound.Count == 1 && cacheFound[0].Text.StartsWith(StepCatalog.Name("shaders") + " have the same output mod", StringComparison.Ordinal),
			"two caches with one output mod: " + string.Join(" | ", cacheFound.Select(i => i.Text)));
		var cacheAndStep = Setup.FromJson(new JsonObject { ["steps"] = new JsonArray(Caches("Shared Output"), Step("synthesis", "output_mod", "Shared Output")) });
		Equal(1, Preflight.SharedOutputs(cacheAndStep, ["shaders", "synthesis"]).Count(), "a cache and another step with one output mod");
		Equal(0, Preflight.SharedOutputs(Setup.FromJson(new JsonObject { ["steps"] = new JsonArray(Caches("One", "Two")) }), ["shaders"]).Count(), "two caches of their own");
		// what a pattern without {output} makes of a new setup's names
		Equal(SetupMaker.OutputName("{profile} outputs", "TexGen Output", "P"), SetupMaker.OutputName("{profile} outputs", "DynDOLOD Output", "P"), "a pattern without {output} names every output alike");
	}

	// what a setup starts on this PC by itself is listed, and marked so that the window asks about it once
	static void T_WhatASetupStartsIsListedAndMarked()
	{
		var s = Setup.FromJson(new JsonObject {
			["mo2"] = new JsonObject { ["exe"] = @"D:\MO2\ModOrganizer.exe", ["profile"] = "P" }, ["beforeRun"] = new JsonArray("first.bat"), ["afterRun"] = "last.bat",
			["steps"] = new JsonArray(
				new JsonObject { ["step"] = "xlodgen", ["on"] = true, ["settings"] = new JsonObject { ["exe"] = @"D:\Tools\xLODGenx64.exe", ["output_mod"] = "xLODGen Output", ["executable"] = "xLODGen" } },
				new JsonObject { ["step"] = "texgen", ["on"] = false, ["settings"] = new JsonObject { ["wrapper"] = @"D:\Tools\wrap.cmd", ["deadline_min"] = 30 } }) }, @"D:\Data\Setups\Mine.json");
		var lines = s.Starts();
		Equal(@"Mod Organizer: D:\MO2\ModOrganizer.exe|a command before each run: first.bat|a command after each run: last.bat|" +
			StepCatalog.Name("xlodgen") + @" (exe): D:\Tools\xLODGenx64.exe|" + StepCatalog.Name("texgen") + @" (wrapper): D:\Tools\wrap.cmd", string.Join("|", lines),
			"Mod Organizer, its commands, and each program a step names - of a step that is off too");
		var mark = s.StartsMark();
		((JsonObject)s.Root["steps"]![1]!)["on"] = true;
		((JsonObject)s.Root["steps"]![0]!["settings"]!)["output_mod"] = "Another Output";
		s.Root["minFreeGb"] = 30;
		Equal(mark, s.StartsMark(), "a step switched, a mod's name, a number: what it starts is the same, and so is its mark");
		s.Root["afterRun"] = "other.bat";
		Check(s.StartsMark() != mark, "another command is another mark");
		Equal("starts.allowed.mine", ToolRunner.App.MainViewModel.StartsAllowedKey(s.File), "kept per setup, by its file's name");
		Equal(2, s.TakeOutCommands(), "brought in from elsewhere, its commands go");
		s.TakeOutWhatStartsPrograms();
		Equal(0, s.Starts().Count, "... and its programs: it starts nothing, and nothing is asked");
		Equal(0, Setup.FromJson(new JsonObject { ["steps"] = new JsonArray() }).Starts().Count, "a setup with nothing in it starts nothing");
		// a program a step starts is one by the setting's name, whatever its file is called
		var odd = Setup.FromJson(new JsonObject { ["steps"] = new JsonArray(new JsonObject { ["step"] = "synthesis", ["settings"] = new JsonObject {
			["exe_path"] = @"D:\Tools\Synthesis\Synthesis", ["fasterngio_exe"] = @"D:\Tools\ngio.bin", ["output_mod"] = "Synthesis Output", ["note"] = "a saver.scr", ["image"] = "Synthesis.exe" } }) });
		Equal(3, odd.Starts().Count, "Synthesis's own program path, a program under any name, a value that ends like one: " + string.Join(" | ", odd.Starts()));
		Check(!odd.Starts().Any(l => l.Contains("(image)", StringComparison.Ordinal)), "the name a tool's process runs under starts nothing, and is not listed");
		Equal(4, odd.TakeOutWhatStartsPrograms().Count, "... and each is taken out of a setup that is brought in (that name with them, as before)");
		Equal("Synthesis Output", Setup.Str(odd.Steps[0].Settings, "output_mod"), "what names no program stays");
	}

	// a setup file from another player: brought in with nothing left that starts a program here, under a name that is free
	static void T_ASetupFromElsewhereIsBroughtInUnderAFreeName()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "bring in " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		try {
			var from = Path.Combine(dir, "Their Setup (shared).json");
			File.WriteAllText(from, """
				{ "name": "Their Setup", "mo2": { "exe": "C:\\Their\\ModOrganizer.exe", "profile": "Default" }, "backups": "C:\\Their\\Backups", "beforeRun": "start.bat",
				  "steps": [ { "step": "texgen", "settings": { "executable": "TexGen", "exe": "C:\\Their\\TexGenx64.exe", "output_mod": "TexGen Output" } } ] }
				""");
			var setups = Path.Combine(dir, "Setups");
			var first = SetupShare.BringIn(from, null, setups);
			Check(first.File == Path.Combine(setups, "Their Setup.json") && File.Exists(first.File), "brought in under its own name: " + string.Join(" | ", first.Lines));
			var read = Setup.Load(first.File!);
			Check(read.Mo2Exe == "" && read.BeforeRun.Count == 0 && read.BackupDir == "" && Setup.Str(read.Steps[0].Settings, "exe") == null, "nothing in it starts a program or names a path of the other PC");
			Equal("TexGen Output", Setup.Str(read.Steps[0].Settings, "output_mod"), "what is no path and no program stays");
			Check(first.Lines.Any(l => l.Contains("its 1 own command(s)", StringComparison.Ordinal)) && first.Lines.Any(l => l.StartsWith("taken out", StringComparison.Ordinal)),
				"what went is said: " + string.Join(" | ", first.Lines));
			var again = SetupShare.BringIn(from, null, setups);
			Check(again.File == null && !again.NoName && again.Lines[0].StartsWith("a setup with that name exists", StringComparison.Ordinal), "a name that is taken is said, and nothing is written");
			var free = SetupShare.BringIn(from, null, setups, true);
			Equal<string?>(Path.Combine(setups, "Their Setup (2).json"), free.File, "the window brings it in under the next free name");
			Equal("Their Setup (2)", Setup.Load(free.File!).Name, "... which is its name inside too");
			Equal<string?>(Path.Combine(setups, "Their Setup (3).json"), SetupShare.BringIn(from, null, setups, true).File, "and the one after it");
			Equal<string?>(Path.Combine(setups, "Mine.json"), SetupShare.BringIn(from, "Mine", setups).File, "a name given for it");
			var kept = SetupShare.BringIn(from, "Kept Here", setups, false, dir);
			Check(Setup.Load(kept.File!).BackupDir == Path.Combine(dir, "Backups") && Setup.Str(Setup.Load(kept.File!).Root, "scratch") == Path.Combine(dir, "Scratch") &&
				kept.Lines.Any(l => l.Contains("where a new setup keeps them", StringComparison.Ordinal)), "its backups and scratch are this program's own, as a new setup's are");
			var store = SetupShare.BringIn(from, "output sizes", setups);
			Check(store.File == null && store.NoName && !File.Exists(Path.Combine(setups, "output sizes.json")), "the name of one of the program's own files is no setup's name");
			Equal<string?>(Path.Combine(setups, "output sizes (2).json"), SetupShare.BringIn(from, "output sizes", setups, true).File, "... the window gives it the next free one");
			var none = SetupShare.BringIn(from, "..", setups);
			Check(none.File == null && none.NoName, "a name with nothing plain in it");
			// brought in from the New setup box: for the Mod Organizer and profile picked there, its outputs named after that profile
			File.WriteAllText(from, """
				{ "name": "Friend", "mo2": { "exe": "", "profile": "Their Profile" }, "outputPattern": "{output} - {profile}", "lodOutputs": ["DynDOLOD Output - Their Profile", "Kept As It Is"],
				  "steps": [ { "step": "texgen", "settings": { "output_mod": "TexGen Output - Their Profile", "disable_during": ["DynDOLOD Output - their profile", "Some Mod"], "deadline_min": 30 } },
				             { "step": "shaders", "settings": { "caches": [ { "shader_mod": "Open Shaders - Their Profile", "output_mod": "Shader Cache - Their Profile" } ] } } ] }
				""");
			var mine = SetupShare.BringIn(from, null, setups, true, dir, new SetupShare.ForProfile(@"D:\MO2\ModOrganizer.exe", @"D:\MO2", "My Profile"));
			var m = Setup.Load(mine.File!);
			Check(m.Mo2Exe == @"D:\MO2\ModOrganizer.exe" && m.Mo2Instance == @"D:\MO2" && m.Profile == "My Profile", "it is made for the Mod Organizer and profile picked: " + string.Join(" | ", mine.Lines));
			Equal("TexGen Output - My Profile", Setup.Str(m.Steps[0].Settings, "output_mod"), "an output named after the other profile is named after this one");
			Equal("DynDOLOD Output - My Profile|Some Mod", string.Join("|", Setup.List(m.Steps[0].Settings, "disable_during")), "... in a list too, whatever its letters' case; a mod of the player's stays");
			Equal("DynDOLOD Output - My Profile|Kept As It Is", string.Join("|", m.LodOutputs), "... and among the LOD outputs");
			var cache = (JsonObject)((JsonArray)m.Steps[1].Settings["caches"]!)[0]!;
			Check(Setup.Str(cache, "output_mod") == "Shader Cache - My Profile" && Setup.Str(cache, "shader_mod") == "Open Shaders - Their Profile", "a cache's output too - never the shader mod it is built for, which is a mod of the player's");
			Check(mine.Lines.Any(l => l.StartsWith("4 output mod name(s) made for the profile \"Their Profile\"", StringComparison.Ordinal)), "how many were named anew is said: " + string.Join(" | ", mine.Lines));
			Equal(30, Setup.Int(m.Steps[0].Settings, "deadline_min"), "what is no name stays");
			var same = SetupShare.BringIn(from, "Same Profile", setups, false, dir, new SetupShare.ForProfile(@"D:\MO2\ModOrganizer.exe", @"D:\MO2", "their profile"));
			Equal("TexGen Output - Their Profile", Setup.Str(Setup.Load(same.File!).Steps[0].Settings, "output_mod"), "for a profile of the same name nothing is named anew");
			File.WriteAllText(from, """{ "name": "..", "mo2": {}, "steps": [] }""");
			Equal<string?>(Path.Combine(setups, "Their Setup (shared).json"), SetupShare.BringIn(from, null, setups).File, "a file whose own name is none is kept under its file's name");
			File.WriteAllText(from, """{ "steps": "none" }""");
			Check(SetupShare.BringIn(from, null, setups).Lines[0].StartsWith("not a setup file", StringComparison.Ordinal), "a file that is no setup");
			Equal("Example", ToolRunner.App.Cli.ImportName("Example. "), "the command line's name for it is the same one");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// what the command line prints once nobody reads it any more is dropped: never an error, never another exit code
	static void T_PrintingToAReaderThatIsGoneIsNoError()
	{
		using var to = new ClosingWriter();
		using var o = new ToolRunner.App.ConsoleAttach.ReaderGone(to);
		o.WriteLine("one");
		o.Write("two");
		o.Write('!');
		Equal("one" + Environment.NewLine + "two!", to.Text, "while it is read, every write goes through");
		to.Gone = true;
		o.WriteLine("three");
		o.Write('x');
		o.Flush();
		to.Gone = false;
		o.WriteLine("four");
		Equal("one" + Environment.NewLine + "two!", to.Text, "after the first write that failed nothing more is tried");
		Equal(1, to.Failed, "... the reader is asked once, not at every line");
	}

	sealed class ClosingWriter() : StringWriter(System.Globalization.CultureInfo.InvariantCulture)
	{
		public bool Gone;
		public int Failed;
		public string Text => ToString();

		public override void Write(char a_value)
		{
			Fail();
			base.Write(a_value);
		}

		public override void Write(string? a_value)
		{
			Fail();
			base.Write(a_value);
		}

		public override void WriteLine(string? a_value)
		{
			Fail();
			base.WriteLine(a_value);
		}

		void Fail()
		{
			if (Gone) {
				++Failed;
				throw new IOException("The pipe is being closed.");
			}
		}
	}

	// the command line: an option its command does not have, one without what it names, a stray word - refused, never passed over
	static void T_TheCommandLineRefusesWhatACommandDoesNotTake()
	{
		static string? Not(params string[] a_args) => ToolRunner.App.Cli.NotTaken(a_args);
		Check(Not("run", "--setup", "x.json", "--form", "texgen")?.StartsWith("--form is no option of run (it takes: --setup, --only, --from, --until, --json)", StringComparison.Ordinal) == true,
			"a misspelt option is said, with the ones the command has: " + Not("run", "--setup", "x.json", "--form", "texgen"));
		Equal("--only needs what it names right after it", Not("run", "--setup", "x.json", "--only"), "an option at the end with nothing after it");
		Equal("--setup needs what it names right after it", Not("run", "--setup", "--only", "texgen"), "an option followed by another option");
		Check(Not("run", "--setup", "x.json", "texgen")?.StartsWith("\"texgen\" is not expected there", StringComparison.Ordinal) == true, "a word that belongs to no option");
		Check(Not("check", "--setup", @"D:\My", @"Setups\x.json")?.Contains("goes in quotes", StringComparison.Ordinal) == true, "a path with a blank typed without quotes");
		Equal("--setup is given twice", Not("run", "--setup", "a.json", "--setup", "b.json"), "one setup per run");
		Equal<string?>(null, Not("queue", "--setup", "a.json", "--setup", "b.json", "--keep-going"), "a queue names as many as it runs");
		Equal<string?>(null, Not("run", "--setup", @"D:\My Setups\x.json", "--only", "addon:My Tool", "--json"), "what the window itself starts a run with");
		Equal<string?>(null, Not("run", "--setup", "x.json", "--from", "texgen", "--until", "dyndolod"), "a run from one step to another");
		Equal<string?>(null, Not("tools", "--mo2", "ModOrganizer.exe", "--instance", "D:\\MO2", "--add", "--json"), "every option of a command at once");
		Equal<string?>(null, Not("status"), "a command alone");
		Equal<string?>(null, Not("addon-check", "my addon.json"), "the one command that takes a word of its own");
		Check(Not("addon-check", "my", "addon.json") != null, "... and only one");
		Equal("--json is no option of stop (it takes none)", Not("stop", "--json"), "a command that takes nothing");
		Equal("--json is no option of lod (it takes: --setup, --step)", Not("lod", "--setup", "x.json", "--step", "grass", "--json"), "--json where a command prints no JSON");
		Equal<string?>(null, Not("frobnicate", "--anything"), "a word that is no command is said to be none elsewhere");
		Equal<string?>(null, Not(), "nothing typed");
		// every command the help line names takes its options from the table (one left out would take anything again) - read off
		// the source, when it is there (the checks above run without it)
		var cliFile = Path.Combine(Path.GetDirectoryName(SourceDir())!, "Runner.App", "Cli.cs");
		if (!File.Exists(cliFile)) {
			Console.WriteLine($"  (the help line's scan skipped: the source is not at {cliFile})");
			return;
		}
		var cli = File.ReadAllText(cliFile);
		var help = System.Text.RegularExpressions.Regex.Match(cli, "Console\\.WriteLine\\(\"Tool Runner - ([^\"]+)\"\\)").Groups[1].Value.Split('|').Select(c => c.Trim()).Where(c => c != "").ToList();
		Check(help.Count >= 20, "the help line's commands were read: " + help.Count);
		foreach (var command in help) {
			Check(Not(command, "--no-such-option") is { } said && said.Contains("is no option of " + command, StringComparison.Ordinal), command + " refuses an option it does not have");
		}
	}

	// a name written twice in one object: the file does not read, said where it is read - never an error at the first look into it
	static void T_ANameTwiceInAFileIsAFileThatDoesNotRead()
	{
		Throws<System.Text.Json.JsonException>(() => Setup.RootOf("""{ "minFreeGb": 20, "steps": [], "minFreeGb": 30 }"""), "a setup with a setting twice");
		Throws<System.Text.Json.JsonException>(() => Setup.RootOf("""{ "steps": [ { "step": "texgen", "settings": { "deadline_min": 60, "deadline_min": 90 } } ] }"""), "... or a step's setting twice");
		Throws<System.Text.Json.JsonException>(() => Setup.Parse(System.Text.Encoding.UTF8.GetBytes("""{ "name": "a", "name": "b" }"""), "x.json"), "... read as the preview reads it");
		Check(Setup.RootOf("""{ "name": "a", "Name": "b", /* a note */ "steps": [], }""") is { Count: 3 }, "two names that differ by their case are two names; a comment and a last comma as before");
		var addOn = AddOns.Problems("""{ "schema": 1, "name": "A", "name": "B" }""");
		Check(addOn.Count == 1 && addOn[0].StartsWith("it is not valid JSON", StringComparison.Ordinal), "an add-on file with a name twice is said to its author: " + string.Join("; ", addOn));
		Check(LauncherStep.SettingsProblem("""{ "Mo2InstancePath": "a", "Mo2InstancePath": "b" }""", "Landscape Fixer") is { } why && why.Contains("does not read as settings", StringComparison.Ordinal),
			"a tool's settings file with one stops its step before anything is changed");
	}

	// A start Windows' management service answered too late may have reached the open Mod Organizer all the same: the tool it
	// starts is waited for and comes back to the run, which puts it on its record (left off it, it held Mod Organizer open)
	static void T_AToolStartedAfterALateAnswerIsTheRuns() => FastReady(() => {
		using var f = new FakeMo2();
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.FromSeconds(9), null),
			Launch = (_, _) => throw new Mo2Control.StartUnansweredException("Windows could not start x (its management service did not answer within 2 minutes)"),
		};
		var looks = 0;
		Equal<int?>(4242, c.LaunchTool("x", () => ++looks >= 3 ? 4242 : 0, TimeSpan.FromSeconds(10), default), "the tool that came is handed back");
		var none = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.FromSeconds(9), null),
			Launch = (_, _) => throw new Mo2Control.StartUnansweredException("late"),
		};
		Equal<int?>(null, none.LaunchTool("x", () => 0, TimeSpan.FromSeconds(2), default), "and none that never came is made up");
	});

	// a ticked mod whose folder would not be read: the player's marks of its plugins are never dropped for it - every mark no
	// other ticked mod's plugin explains is set as they had it - and the log says so
	static void T_APlayersPluginMarksStayWhenAModDoesNotRead()
	{
		using var f = new FakeMo2();
		f.Mod("Locked Mod", ("Locked.esp", false, []));
		File.AppendAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Locked Mod\r\n");
		File.AppendAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "*Locked.esp\r\n");
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var ctx = StepContext(f, "landscapefixer");
		var locked = new DirectoryInfo(Path.Combine(f.Mods, "Locked Mod"));
		var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
		var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
		var acl = System.IO.FileSystemAclExtensions.GetAccessControl(locked);
		acl.AddAccessRule(deny);
		System.IO.FileSystemAclExtensions.SetAccessControl(locked, acl);
		try {
			if (Plugins.TryInMod(locked.FullName, out _)) {
				Console.WriteLine("      (the folder still reads here: skipped)");
				return;
			}
			Runner.ReapplyTheirs(ctx, new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase) { ["Locked Mod"] = '+' },
				new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["Locked.esp"] = false, ["BaseMod.esp"] = false });
			var plugins = f.Text("plugins.txt");
			Check(plugins.Contains("\nLocked.esp", StringComparison.Ordinal) && !plugins.Contains("*Locked.esp", StringComparison.Ordinal), "its plugin is as the player had it: " + plugins);
			Check(plugins.Contains("*BaseMod.esp", StringComparison.Ordinal), "a plugin a ticked mod that reads holds is left to that mod");
			ctx.Log.Dispose();
			Check(File.ReadAllText(Path.Combine(ctx.Journal.Folder, "log.txt")).Contains("Locked Mod could not be read", StringComparison.Ordinal), "and the log names the mod");
		} finally {
			acl.RemoveAccessRule(deny);
			System.IO.FileSystemAclExtensions.SetAccessControl(locked, acl);
		}
	}

	// More of a setup file that comes from anywhere, read as meant: the entry of Mod Organizer's list a step starts is among what
	// the setup starts; a negative fraction is no number; a shader step's cache named both ways is one output; lodOutputs as one
	// name is named after the profile too; a file that is no setup is said so, apart from a name that is taken
	static void T_MoreOfASetupFromAnywhereIsReadAsMeant()
	{
		var withEntry = Setup.FromJson(new JsonObject { ["mo2"] = new JsonObject(), ["steps"] = new JsonArray(new JsonObject { ["step"] = "pgpatcher", ["on"] = true, ["settings"] = new JsonObject { ["executable"] = "PGPatcher" } }) });
		Check(withEntry.Starts().Any(l => l.Contains("PGPatcher", StringComparison.Ordinal) && l.Contains("Mod Organizer's entry", StringComparison.Ordinal)), "a step's entry is among what it starts: " + string.Join(" | ", withEntry.Starts()));
		var changed = Setup.FromJson(new JsonObject { ["mo2"] = new JsonObject(), ["steps"] = new JsonArray(new JsonObject { ["step"] = "pgpatcher", ["on"] = true, ["settings"] = new JsonObject { ["executable"] = "Something Else" } }) });
		Check(withEntry.StartsMark() != changed.StartsMark(), "another entry asks again");
		Equal(60, new StepSettings(new JsonObject { ["deadline_min"] = -0.5 }).Int("deadline_min", 60), "a negative fraction is no number: the default stands");
		Equal(1, new StepSettings(new JsonObject { ["deadline_min"] = 0 }).Int("deadline_min", 60), "a zero of minutes is one");
		Equal(2, new StepSettings(new JsonObject { ["deadline_min"] = 2.7 }).Int("deadline_min", 60), "a positive fraction is made whole");
		var shaders = Setup.FromJson(new JsonObject { ["mo2"] = new JsonObject(), ["steps"] = new JsonArray(new JsonObject { ["step"] = "shaders", ["on"] = true,
			["settings"] = new JsonObject { ["output_mod"] = "Cache", ["shader_mod"] = "Shader Mod", ["caches"] = new JsonArray(new JsonObject { ["shader_mod"] = "Shader Mod", ["output_mod"] = "Cache" }) } }) });
		Equal(0, Preflight.SharedOutputs(shaders, ["shaders"]).Count(), "one cache named both ways is one output, never shared with itself");
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "lod outputs " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		try {
			var from = Path.Combine(dir, "shared.json");
			File.WriteAllText(from, """{ "name": "Shared", "mo2": { "profile": "Old" }, "lodOutputs": "DynDOLOD Output - Old", "steps": [ { "step": "dyndolod", "on": true, "settings": { "output_mod": "DynDOLOD Output - Old" } } ] }""");
			var setups = Path.Combine(dir, "Setups");
			var got = SetupShare.BringIn(from, null, setups, true, dir, new SetupShare.ForProfile(@"D:\MO2\ModOrganizer.exe", @"D:\MO2", "New"));
			Check(got.File != null, "brought in: " + string.Join(" | ", got.Lines));
			var root = Setup.Load(got.File!).Root;
			Equal("DynDOLOD Output - New", root["lodOutputs"]?.ToString(), "lodOutputs given as one name follows the step's output");
			var notOne = Path.Combine(dir, "not one.json");
			File.WriteAllText(notOne, """{ "steps": "none" }""");
			var refused = SetupShare.BringIn(notOne, "Not One", setups, false, dir);
			Check(refused.File == null && refused.NotASetup && !refused.NoName, "a file that is no setup is said to be none: " + string.Join(" | ", refused.Lines));
			var taken = SetupShare.BringIn(from, Path.GetFileNameWithoutExtension(got.File!), setups, false, dir);
			Check(taken.File == null && !taken.NotASetup, "a name that is taken is no fault of the file");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// A growing log read by what each look added gives only that: a line still being written comes once, with the look that
	// ends it - never also from the look that found it unfinished
	static void T_AGrowingLogGivesAnUnfinishedLineOnce()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "growing " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		try {
			var file = Path.Combine(dir, "tool.log");
			File.WriteAllText(file, "progress");
			var g = new GrowingText { KeepAll = false };
			Equal("", g.Read(file), "an unfinished first line is not given yet");
			Equal("progress", g.Unfinished, "it is the unfinished one");
			File.AppendAllText(file, "\nnext");
			Equal("progress\n", g.Read(file), "it comes once, with the look that ends it");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// a bug report is made when a file it would hold cannot be read: that file's place says why
	static void T_ABugReportIsMadeWithAFileItCannotRead()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var j = new Journal { Id = "r1", Folder = Path.Combine(Runner.RunsDir(f.Data), "r1"), SetupFile = Path.GetFullPath(f.SetupFile), SetupName = "Test", Profile = "Test",
			Started = DateTime.Now, State = RunState.Finished };
		Directory.CreateDirectory(j.Folder);
		j.Save();
		var outDir = Path.Combine(f.Root, "Reports");
		string zip;
		// (held open by nobody else's leave: no read of it goes through while this handle stands)
		using (new FileStream(f.SetupFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
			zip = BugReport.Make(f.Data, j, outDir, "1.0");
		}
		using var z = System.IO.Compression.ZipFile.OpenRead(zip);
		using var setupEntry = new StreamReader(z.GetEntry("setup.json")!.Open());
		Check(setupEntry.ReadToEnd().StartsWith("(this file could not be read", StringComparison.Ordinal), "the setup's place says it could not be read");
	}

	// The words of every StepFailedException one C# file makes, in the order they stand: "new StepFailedException(...)", and a
	// target-typed "new(...)" where the type it makes is a StepFailedException - a helper declared to return one (by => or by
	// return), or a local or field of that type. A call in a comment, and one that only hands on a text made elsewhere, has none
	static List<string> StepFailureTexts(string a_src)
	{
		var at = new SortedDictionary<int, int>();
		foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(a_src, @"\bnew StepFailedException\(")) {
			at[m.Index] = m.Index + m.Length;
		}
		// a local or field of the type, or a helper that gives one with =>
		foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(a_src, @"\bStepFailedException\??\s+\w+\s*(?:=|\([^()]*\)\s*=>)\s*(new)\(")) {
			at[m.Groups[1].Index] = m.Index + m.Length;
		}
		// a helper with a body: each "return new(" up to its closing bracket
		foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(a_src, @"\bStepFailedException\??\s+\w+\s*\([^()]*\)\s*\{")) {
			var depth = 1;
			var end = m.Index + m.Length;
			while (end < a_src.Length && depth > 0) {
				depth += a_src[end] == '{' ? 1 : a_src[end] == '}' ? -1 : 0;
				++end;
			}
			foreach (System.Text.RegularExpressions.Match r in System.Text.RegularExpressions.Regex.Matches(a_src[(m.Index + m.Length)..end], @"\breturn\s+(new)\(")) {
				var from = m.Index + m.Length;
				at[from + r.Groups[1].Index] = from + r.Index + r.Length;
			}
		}
		var texts = new List<string>();
		foreach (var (start, open) in at) {
			var line = a_src.LastIndexOf('\n', start) + 1;
			if (a_src.AsSpan(line, start - line).Contains("//", StringComparison.Ordinal)) {
				continue;
			}
			// (a failure that only hands on a text made elsewhere - a tool's own box, a check's finding - has no words of its own here)
			var text = string.Join(" ", ReadCall(a_src, open).Strings);
			if (text.Trim().Length >= 8) {
				texts.Add(text);
			}
		}
		return texts;
	}

	// ... and every text a step fails with: its own words, or the tool's, come with a code and what to do
	static void T_EveryFailureOfAStepIsInTheCatalog()
	{
		var core = SourceDir();
		if (!Directory.Exists(core)) {
			Console.WriteLine($"  (skipped: the source is not at {core})");
			return;
		}
		var missing = new List<string>();
		var seen = 0;
		// (the scan itself, on a made-up file: each way a failure is made is found, a target-typed new() in a helper included)
		var fixture = StepFailureTexts("""
			static StepFailedException Lost() => new("the fixture's helper found no usable input");
			static StepFailedException Gone() { return new("the fixture's other helper found nothing"); }
			void A() { StepFailedException e = new("the fixture's local found nothing either"); throw e; }
			void B() => throw new StepFailedException("the fixture's plain one");
			// new StepFailedException("a comment is no failure")
			""");
		Equal("the fixture's helper found no usable input | the fixture's other helper found nothing | the fixture's local found nothing either | the fixture's plain one",
			string.Join(" | ", fixture), "every form of a step's failure is found, a comment's is not");
		foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains(@"\obj\", StringComparison.Ordinal) && !p.Contains(@"\bin\", StringComparison.Ordinal))) {
			foreach (var text in StepFailureTexts(File.ReadAllText(file))) {
				++seen;
				if (RunnerErrors.Find(text + " ") == null) {
					missing.Add($"{Path.GetFileName(file)}: {text.Trim()}");
				}
			}
		}
		Check(seen >= 120, $"the scan found the steps' failures ({seen})");
		Check(missing.Count == 0, $"{missing.Count} failure(s) of a step with no catalog entry (RunnerErrors.cs) - " + string.Join(" || ", missing.Take(6)));
		// the nearer entry wins: a limit before "the window did not come", an empty result before the tool's name
		Equal("TR-408", RunnerErrors.Find("xLODGen's options did not appear within the step's 120 minute limit")?.Code, "the step's limit");
		Equal("TR-433", RunnerErrors.Find("xLODGen's options did not appear, and the tool's log said nothing new for 15 minutes")?.Code, "a window that never came");
		Equal("TR-411", RunnerErrors.Find("the grass cache looks empty (3 files)")?.Code, "almost nothing made");
		Equal("TR-433", RunnerErrors.Find("DynDOLOD: no Start button in its options window (DynDOLOD needs Expert mode)")?.Code, "the window, not the tool's own words");
		Equal(RunnerErrors.ToolsOwnWords, RunnerErrors.Find("Pandora: something only Pandora would say")?.Code, "what a tool itself said");
		Equal(RunnerErrors.ToolsOwnWords, RunnerErrors.Find("DynDOLOD reported an error: Error: could not start the export, did not write the file")?.Code,
			"the tool's own words choose no entry of the runner's");
		Equal(RunnerErrors.ToolsOwnWords, RunnerErrors.All[^1].Code, "and that entry is the last one asked");
		// a tool's own documented error goes before the catalog's general words for "the tool said so"
		var memory = Explain.For("dyndolod", "DynDOLOD: Error: Out of memory");
		Check(memory is { Code: null, Help: "https://dyndolod.info/FAQ" }, "the tool's own help first: " + memory);
		Check(Explain.For("pandora", "Pandora: something only Pandora would say") is { Code: RunnerErrors.ToolsOwnWords }, "the general words where the tool's help has none");
		// a place that is no place is named at the check, where it was read as "end"
		var odd = Setup.FromJson(new JsonObject { ["placement"] = JsonNode.Parse("[[\"Mine.esp\", \"afer:Skyrim.esm\"], [\"Other.esp\", \"after:Skyrim.esm\"], [\"Last.esp\", \"end\"], [\"Top.esp\", \"last_master\"]]") }).Problems();
		var named = odd.Where(p => p.Contains("a place is last_master", StringComparison.Ordinal)).ToList();
		Check(named.Count == 1 && named[0].Contains("Mine.esp", StringComparison.Ordinal) && RunnerErrors.Find(named[0])?.Code == "TR-118", "the one slip is named, the three places are not: " + string.Join(" | ", odd));
		// a plugin named twice under "placeAfter" in other letters is read once, the first time - never a throw after a step
		var twice = Setup.FromJson(new JsonObject { ["placeAfter"] = JsonNode.Parse("{\"DynDOLOD.esm\": \"PGPatcher.esp\", \"dyndolod.esm\": \"Other.esp\", \"Lone.esp\": 3}") });
		Equal(2, twice.PlaceAfter.Count, "each plugin once (a number is a value too)");
		Equal("PGPatcher.esp", twice.PlaceAfter["DYNDOLOD.ESM"], "the first one, found in any letter case");
	}

	// ... and that guard is a real one: a planted refusal nobody put in the catalog is caught, a note and a known one are not
	static void T_ARefusalWithoutAnEntryIsCaught()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "refusals " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		try {
			File.WriteAllText(Path.Combine(dir, "Planted.cs"), """
				class Planted
				{
					IEnumerable<Issue> Check(string a_x, bool a_soft)
					{
						yield return new Issue(Key, true, $"the moon is in the wrong phase ({a_x}) - wait a week");
						yield return new Issue(Key, false, "the tide is out, which only slows things");
						yield return new Issue("", true, "the game is running (" + string.Join(", ", a_x) + ") - close it first");
						yield return new Issue(Key, a_soft, "decided elsewhere whether this one stops");
					}
					// return Refuse("a comment is no refusal");
					RunOutcome Go() => Refuse($"the stars said no: {1 + (2)} \"quoted\" (and bracketed)");
				}
				""");
			var missing = RefusalsNotInTheCatalog(dir, out var seen);
			Equal(3, seen, "the two stopping findings and the refusal were read, the note, the undecided one and the comment were not");
			Equal(2, missing.Count, "the two nobody put in the catalog: " + string.Join(" | ", missing));
			Check(missing.Any(x => x.Contains("the moon is in the wrong phase ( ... ) - wait a week", StringComparison.Ordinal)), "a finding, with a gap where its value goes: " + missing[0]);
			Check(missing.Any(x => x.Contains("the stars said no:  ...  \"quoted\" (and bracketed)", StringComparison.Ordinal)), "a refusal read to the bracket that closes it: " + string.Join(" | ", missing));
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// the runner's own problems and the tools' documented errors answer through one door
	static void T_AProblemComesWithWhatToDo()
	{
		var game = Explain.For("", "the game is running (SkyrimSE.exe) - close it first");
		Check(game is { Code: "TR-203", Help: null } && game.Todo.Contains("Close the game", StringComparison.Ordinal), "the runner's own");
		var memory = Explain.For("dyndolod", "DynDOLOD's log ended with: Out of memory");
		Check(memory is { Code: null } && memory.Help == "https://dyndolod.info/FAQ" && memory.Todo.Contains("page file", StringComparison.Ordinal), "a tool's own, from its documentation");
		Check(Explain.For("pandora", "something nobody has seen before") == null, "nothing is made up for what nobody knows");
		// the nearer entry wins where two share words
		Equal("TR-407", RunnerErrors.Find("Steam did not start within 3 minutes - start it, then start the run again")?.Code, "Steam's own, not the tool start's");
		Equal("TR-406", RunnerErrors.Find("DynDOLOD did not start within 3 minutes")?.Code, "a tool that did not come up");
		Equal("TR-116", RunnerErrors.Find("could not start: prepare.cmd")?.Code, "a setup's own command");
		Equal("TR-111", RunnerErrors.Find("Mod Organizer's program is not set")?.Code, "the setup's folder, not a step's setting");
		Equal("TR-410", RunnerErrors.Find("the step's setting \"executable\" is not set")?.Code, "a step's setting");
		var lines = Explain.Lines("", "another Mod Organizer is open (C:\\Other\\ModOrganizer.exe) - close it first");
		Check(lines.Count == 2 && lines[1].StartsWith("  What to do: ", StringComparison.Ordinal) && lines[1].EndsWith("[TR-211]", StringComparison.Ordinal), "as lines for a person: " + string.Join(" | ", lines));
	}

	// the program's own small files beside the setups are no setups; an output two steps name is
	// counted once; a shader cache (most runs build none) never refuses a start; a tool's own words are never taken for the runner's
	static void T_StoreFilesSharedOutputsAndAToolsOwnWords()
	{
		Check(Setup.IsStore(@"C:\x\Setups\tool versions.json") && Setup.IsStore(@"C:\x\Setups\Output Sizes.json") && !Setup.IsStore(@"C:\x\Setups\My Profile.json"),
			"the tools' versions and the outputs' sizes are the program's own files, a setup is not");
		using var f = new FakeMo2();
		var lod = Path.Combine(f.Root, "lod");
		var setup = f.WriteSetup(
			("shaders", new JsonObject { ["caches"] = new JsonArray(new JsonObject { ["shader_mod"] = "Base Mod", ["output_mod"] = "Output A" }) }),
			("xlodgen", new JsonObject { ["output_mod"] = "Output B", ["output_dir"] = lod }),
			("acmos", new JsonObject { ["then_move_to_mod"] = "Output B", ["reads"] = lod }));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		const long gb = 1L << 30;
		var probes = new DiskForecast.Probes(_ => "X", _ => 100 * gb, _ => 10 * gb);
		var parts = DiskForecast.Build(setup, mo2, setup.EnabledSteps, probes).Disks.Single().Parts;
		Equal(1, parts.Count(p => p.Needed && p.What.Contains("Output B", StringComparison.Ordinal)), "the terrain LOD two steps name is one output");
		Check(parts.Any(p => p.What.Contains("Output A", StringComparison.Ordinal)) && parts.Where(p => p.What.Contains("Output A", StringComparison.Ordinal)).All(p => !p.Needed),
			"a shader cache's room is told, and never refuses a start");
		setup.Root["shaderRebuild"] = "always";
		Check(DiskForecast.Build(setup, mo2, setup.EnabledSteps, probes).Disks.Single().Parts.Any(p => p.Needed && p.What.Contains("Output A", StringComparison.Ordinal)),
			"unless the setup rebuilds the caches every run");
		Equal(RunnerErrors.ToolsOwnWords, RunnerErrors.Find("DynDOLOD: the output path is not set in this preset, and there are no steps left")?.Code,
			"a tool's own words are told as the tool's own, never taken for one of the runner's refusals");
		Equal("TR-112", RunnerErrors.Find("the setup has no steps")?.Code, "the setup's own are");
	}

	// a kept file whose backup is on a drive that is not there is asked
	// again; a folder half moved into the backup gets its part back beside what stayed; the cache that stood in overwrite never
	// comes back over a new cache; the grass wrapper's exit line stands alone; the window says a run ended only once it is over
	static void T_BackupsThatAreGoneAndHalfMovedFoldersComeRight()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var ctx = StepContext(f, "landscapefixer");
		using var ctxLog = ctx.Log;
		var ini = Path.Combine(f.Root, "tool.ini");
		File.WriteAllText(ini, "set for the run");
		if (Enumerable.Range('D', 23).Select(c => (char)c + @":\").FirstOrDefault(d => !Directory.Exists(d)) is { } noDrive) {
			ctx.Journal.Kept.Add(new KeptFile { Path = ini, Backup = Path.Combine(noDrive, "backup", "1 tool.ini") });
			Equal(1, StepKit.PutFilesBack(ctx).Count, "a backup on a drive that is not there: not put back");
			Check(!ctx.Journal.Kept[0].Done, "and asked again when the drive is back");
			ctx.Journal.Kept.Clear();
		}
		ctx.Journal.Kept.Add(new KeptFile { Path = ini, Backup = Path.Combine(f.Backups, "cleared away", "1 tool.ini") });
		Equal(0, StepKit.PutFilesBack(ctx).Count, "a backup that was cleared away holds no later put-back up");
		Check(ctx.Journal.Kept[0].Done && File.ReadAllText(ini) == "set for the run", "the file is left as it is");

		var grass = Path.Combine(f.Overwrite, "grass");
		var parked = Path.Combine(ctx.BackupFolder, "outputs", "overwrite grass (before)");
		Directory.CreateDirectory(grass);
		Directory.CreateDirectory(parked);
		File.WriteAllText(Path.Combine(grass, "stayed.cgid"), "the player's, never moved");
		File.WriteAllText(Path.Combine(parked, "moved.cgid"), "the player's, moved before the copy failed");
		ctx.Journal.Parked.Add(new ParkRecord { Step = "grass", Mod = "overwrite grass (before)", From = grass, To = parked, IsFolder = true });
		Equal(0, Runner.PutBackParked(ctx, "grass").Count, "a folder half moved into the backup is put back");
		Check(File.Exists(Path.Combine(grass, "stayed.cgid")) && File.Exists(Path.Combine(grass, "moved.cgid")), "both parts stand in overwrite again");
		Check(!Directory.Exists(Path.Combine(ctx.BackupFolder, "unfinished")), "nothing of the player's is taken for a half-written output");

		var owCache = Path.Combine(f.Overwrite, "ShaderCache");
		var oldCache = Path.Combine(ctx.BackupFolder, "outputs", "overwrite ShaderCache (before)");
		Directory.CreateDirectory(oldCache);
		File.WriteAllText(Path.Combine(oldCache, "old.bin"), "the cache from before the run");
		ctx.Journal.Parked.Add(new ParkRecord { Step = "shaders", Mod = "overwrite ShaderCache (before)", From = owCache, To = oldCache, IsFolder = true, Moved = true });
		var cache = new ShaderStep.Cache("Base Mod", "Output A", null);
		ShaderStep.Keep(ctx, [cache], cache, ctx.Profile.States());
		Equal(0, Runner.PutBackParked(ctx, "shaders").Count, "a later failure in the shaders step");
		Check(!File.Exists(Path.Combine(owCache, "old.bin")), "never brings the old cache back over the one just built");

		var wrapper = GrassStep.WrapperText(@"C:\x\FasterNGIO.exe", "--world all", @"C:\x\output.txt");
		Check(wrapper.Contains("set TRCODE=%ERRORLEVEL%\r\necho.>>", StringComparison.Ordinal) && wrapper.Contains("echo exit code %TRCODE% >>", StringComparison.Ordinal),
			"the wrapper writes a line break before its exit line: " + wrapper);
		Equal("0", GrassStep.WrapperExit("started\r\nGenerating 100%\r\nexit code 0 \r\n"), "which is then read as the end");

		Check(!ToolRunner.App.MainViewModel.RunEnded(true, true, RunState.Finished), "finished and still at its last chores: not said yet");
		Check(ToolRunner.App.MainViewModel.RunEnded(true, false, RunState.Finished), "over, with its verdict written: said");
		Check(ToolRunner.App.MainViewModel.RunEnded(true, false, RunState.Failed), "a run that failed is said too");
		Check(!ToolRunner.App.MainViewModel.RunEnded(false, false, RunState.Finished), "a run that was over at the first look is not news");
		Check(!ToolRunner.App.MainViewModel.RunEnded(true, false, RunState.Running), "an engine that died is the interrupted run's band, not this notice");
	}

	// a run is named by the second it began: one begun within the same second never takes the earlier one's folder (its record and
	// its copy of the order files - what an undo of the earlier run puts back)
	static void T_TwoRunsInOneSecondKeepTheirOwnRecords()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var runs = new List<RunOutcome>();
		for (var i = 0; i < 3; ++i) {
			runs.Add(Runner.Run(new RunRequest(f.SetupFile, f.Data)));
			Equal(RunState.Finished, runs[^1].State, runs[^1].Verdict);
		}
		Equal(3, runs.Select(r => r.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "three runs, three record folders");
		Equal(3, Journal.All(Runner.RunsDir(f.Data)).Count(), "and three records");
		Check(runs.All(r => Directory.Exists(Path.Combine(r.Folder!, "profile before"))), "each with its own copy of the order files");
	}

	// a step whose own check stops a run
	sealed class BlockedStep(string a_key, string a_why) : IStep
	{
		public string Key => a_key;
		public void Run(RunContext a_ctx, StepSettings a_s) => throw new InvalidOperationException("must not run");
		public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s) => [new Issue(a_key, true, a_why), new Issue(a_key, false, "a note beside it")];
	}

	// a dry run goes through the very gate a start does, says what the start would, and writes nothing anywhere
	static void T_ADryRunSaysWhatAStartWouldAndChangesNothing()
	{
		using var f = new FakeMo2();
		var ran = false;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => ran = true));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		static Dictionary<string, (long, DateTime)> Tree(string a_dir) =>
			Directory.EnumerateFiles(a_dir, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
		var req = new RunRequest(f.SetupFile, f.Data);
		var before = Tree(f.Root);
		var dry = DryRun.Make(req);
		Check(dry.Starts && dry.Preview != null && !ran, "it would start, and nothing ran: " + string.Join("; ", dry.Refusals.Select(x => x.Text)));
		var lines = dry.Lines(_ => null);
		Check(lines[0].Contains("would go ahead", StringComparison.Ordinal) && lines.Any(l => l.Contains("Landscape Fixer", StringComparison.Ordinal)) && lines[^1] == "Nothing has been changed yet.",
			"in words: " + string.Join(" | ", lines));
		var after = Tree(f.Root);
		Check(before.Count == after.Count && before.All(p => after.TryGetValue(p.Key, out var a) && a == p.Value), "not one file made, changed or removed");
		using (var free = Runner.HoldIfFree()) {
			Check(free != null, "the run lock is let go");
		}

		// every refusal reads as the start's own would
		void Same(string a_what)
		{
			var d = DryRun.Make(req);
			var r = Runner.Run(req);
			Check(!d.Starts && r.Folder == null, a_what + ": both refuse");
			Equal(r.Verdict, "not started: " + string.Join("; ", d.Refusals.Select(x => x.Step == "" ? x.Text : $"{StepCatalog.Name(x.Step)}: {x.Text}")), a_what);
		}
		StepRegistry.Replace(new BlockedStep("landscapefixer", "its tool is nowhere to be found"));
		Same("a step's own check");
		var blocked = DryRun.Make(req);
		Check(blocked.Refusals.Single().Step == "landscapefixer" && blocked.Notes.Any(n => n.Text == "a note beside it") && blocked.Preview != null,
			"the step is named, its note kept apart, and what the run would do is still shown");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => ran = true));
		Preflight.GameCheck = () => ["SkyrimSE.exe"];
		try {
			Same("the game running");
			Check(DryRun.Make(req).Lines(_ => null).Any(l => l.Contains("What to do: Close the game", StringComparison.Ordinal)), "each reason with what to do");
		} finally {
			Preflight.GameCheck = () => [];
		}
		// while another run goes, that is the one answer
		using (var holding = new ManualResetEventSlim())
		using (var done = new ManualResetEventSlim()) {
			var other = new Thread(() => {
				using var h = Runner.HoldIfFree();
				holding.Set();
				done.Wait();
			});
			other.Start();
			holding.Wait();
			var busy = DryRun.Make(req);
			done.Set();
			other.Join();
			Check(!busy.Starts && busy.Refusals.Single().Text.Contains("another run is going", StringComparison.Ordinal) && busy.Preview == null, "another run going is said, and nothing else");
		}
		var cut = new Journal { Id = "2026-01-01 00-00-00 Test", Folder = Path.Combine(Runner.RunsDir(f.Data), "2026-01-01 00-00-00 Test"), SetupFile = f.SetupFile, SetupName = "Test",
			Profile = "Test", State = RunState.Running, Started = DateTime.Now.AddHours(-1) };
		Directory.CreateDirectory(cut.Folder);
		cut.Save();
		Same("an earlier run that was cut off");
		Directory.Delete(cut.Folder, true);
		File.WriteAllText(f.SetupFile, "{ not a setup");
		Same("a setup that does not read");
		Check(!ran, "and through all of it nothing ran");
	}

	// a run that ended is summed up: for the window's card, the end of the simple log and the command line
	static void T_ARunThatEndedIsSummedUp()
	{
		using var f = new FakeMo2();
		var realDyndolod = StepRegistry.For("dyndolod")!;
		var realPandora = StepRegistry.For("pandora")!;
		try {
			StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
			StepRegistry.Replace(new FakeStep("dyndolod", (ctx, s) => throw new StepFailedException("DynDOLOD's log ended with: Out of memory")));
			StepRegistry.Replace(new FakeStep("pandora", (ctx, s) => { }));
			f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("dyndolod", new JsonObject { ["output_mod"] = "Output B" }), ("pandora", []));
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Equal(RunState.Failed, r.State, r.Verdict);
			var j = Journal.Load(r.Folder!);
			var s = RunSummary.Of(j);
			string Row(string a_label) => string.Join(" | ", s.Rows.Where(x => x.Label == a_label).Select(x => x.Text));
			Check(s.Kind == "bad" && s.Title.StartsWith("Stopped by a problem after ", StringComparison.Ordinal), s.Title);
			Check(Row("Steps") == "1 of 3 finished; DynDOLOD did not finish; not reached: Pandora Behaviour Engine+", Row("Steps"));
			Check(Row("Why").Contains("Out of memory", StringComparison.Ordinal) && Row("What to do").Contains("page file", StringComparison.Ordinal), "why, and the tool's own advice: " + Row("What to do"));
			Check(Row("New output").StartsWith("Output A (", StringComparison.Ordinal), "what it built, with its size: " + Row("New output"));
			Check(Row("Next").Contains("Run again from DynDOLOD", StringComparison.Ordinal), Row("Next"));
			var simple = File.ReadAllText(Path.Combine(r.Folder!, RunLog.SimpleName));
			Check(simple.Contains(s.Title, StringComparison.Ordinal) && simple.Contains("  Next: ", StringComparison.Ordinal), "the simple log ends with the same summary");

			StepRegistry.Replace(new FakeStep("dyndolod", (ctx, s) => WriteOutput(ctx, "dyndolod", "Output B", "OutB.esp")));
			r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Equal(RunState.Finished, r.State, r.Verdict);
			j = Journal.Load(r.Folder!);
			s = RunSummary.Of(j);
			Check(s.Kind == "good" && s.Title.StartsWith("Finished in ", StringComparison.Ordinal) && Row("Steps") == "3 of 3 finished", s.Title + " / " + Row("Steps"));
			Check(Row("New outputs").Contains("Output A", StringComparison.Ordinal) && Row("New outputs").Contains("Output B", StringComparison.Ordinal), Row("New outputs"));
			Check(Row("Next").StartsWith("Nothing left to do", StringComparison.Ordinal) && Row("Old outputs").Contains(j.BackupDir, StringComparison.Ordinal), Row("Next") + " / " + Row("Old outputs"));
			Check(Row("Why") == "" && Row("What to do") == "", "nothing to explain after a run that finished");
			// a run that was cut off says what to press
			j.State = RunState.Running;
			Check(RunSummary.Of(j) is { Kind: "bad", Title: "Did not finish" } cutOff && cutOff.Rows.Any(x => x.Label == "Next" && x.Text.Contains("Put my profile back", StringComparison.Ordinal)), "a run that was cut off");
			Check(RunSummary.Of(j).Rows.Any(x => x.Label.StartsWith("New output", StringComparison.Ordinal) && x.Text.Contains("switched on once your profile is put back", StringComparison.Ordinal)),
				"its outputs are built, not yet on");
			j.Undoing = true;
			Check(RunSummary.Of(j).Rows.Any(x => x.Label.StartsWith("New output", StringComparison.Ordinal) && x.Text.Contains("sets them aside", StringComparison.Ordinal)),
				"an undo that was cut off sets them aside when it is finished - never 'switched on'");
			j.Undoing = false;
		} finally {
			StepRegistry.Replace(realDyndolod);
			StepRegistry.Replace(realPandora);
		}
	}

	// "status": what is going right now, read off the records - idle, a step working, the run waiting for the player, a run cut off
	static void T_StatusSaysWhatIsGoing()
	{
		using var f = new FakeMo2();
		var idle = RunStatus.Take(f.Data);
		Check(idle is { State: "idle", SafeToClose: true, Run: null, Last: null }, "nothing has run");
		RunStatus.Snapshot? working = null, waiting = null;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			ctx.Journal.Step("landscapefixer")!.Activity = "building";
			ctx.Journal.Step("landscapefixer")!.Progress = 0.5;
			ctx.Journal.Save();
			working = RunStatus.Take(f.Data);
			ctx.Journal.Step("landscapefixer")!.Activity = Runner.WaitingPrefix + "press Done in the window";
			ctx.Journal.Save();
			waiting = RunStatus.Take(f.Data);
			ctx.Journal.Step("landscapefixer")!.Activity = "";
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		Check(working is { State: "running", SafeToClose: false, Waiting: "" } && working.Step?.Key == "landscapefixer", "a step working: " + working?.State);
		Check(RunStatus.Lines(working!).Any(l => l.Contains("now: Landscape Fixer 50 % - building", StringComparison.Ordinal)), string.Join(" | ", RunStatus.Lines(working!)));
		Check(waiting is { State: "waiting", Waiting: "press Done in the window" }, "the run waiting for the player: " + waiting?.State);
		var after = RunStatus.Take(f.Data, f.SetupFile);
		Check(after is { State: "idle", SafeToClose: true } && after.Last?.State == RunState.Finished, "idle again, with the last run");
		Check(RunStatus.Lines(after).Any(l => l.Contains("last run: Test", StringComparison.Ordinal)) && RunStatus.Lines(after)[^1].Contains("safe to close", StringComparison.Ordinal), string.Join(" | ", RunStatus.Lines(after)));
		var json = JsonNode.Parse(Reports.Text(Reports.Status(working!, "1.0")))!;
		Check(json["state"]!.ToString() == "running" && json["currentStep"]!.ToString() == "landscapefixer" && json["safeToClose"]!.GetValue<bool>() == false &&
			json["run"]!["steps"]![0]!["progress"]!.GetValue<double>() == 0.5 && json["version"]!.ToString() == "1.0", "the same as JSON");
		// a run that was cut off (its record still says running, its program is gone)
		var cut = new Journal { Id = "2099-01-01 00-00-00 Test", Folder = Path.Combine(Runner.RunsDir(f.Data), "2099-01-01 00-00-00 Test"), SetupFile = f.SetupFile, SetupName = "Test",
			Profile = "Test", State = RunState.Running, Started = DateTime.Now.AddHours(-1) };
		Directory.CreateDirectory(cut.Folder);
		cut.Save();
		var interrupted = RunStatus.Take(f.Data);
		Check(interrupted is { State: "interrupted", Run: null } && interrupted.Interrupted != null && RunStatus.Lines(interrupted)[0].Contains("restore", StringComparison.Ordinal), "a run that was cut off");
	}

	// the command line's --json: the names a script reads stay, and a stopping finding carries its code and what to do
	static void T_JsonOutputKeepsItsNames()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => { }));
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		static void Has(JsonNode a_o, params string[] a_names)
		{
			foreach (var n in a_names) {
				Check(a_o.AsObject().ContainsKey(n), $"the name \"{n}\" is there: {string.Join(", ", a_o.AsObject().Select(p => p.Key))}");
			}
		}
		Preflight.GameCheck = () => ["SkyrimSE.exe"];
		try {
			var check = JsonNode.Parse(Reports.Text(Reports.Check(setup, Preflight.Run(setup, mo2, ["landscapefixer"]))))!;
			Has(check, "schema", "program", "command", "setup", "ready", "issues");
			Check(check["schema"]!.GetValue<int>() == Reports.Schema && check["command"]!.ToString() == "check" && check["program"]!.ToString() == Brand.Name && !check["ready"]!.GetValue<bool>(), "the head");
			var game = check["issues"]!.AsArray().Single(i => i!["text"]!.ToString().Contains("the game is running", StringComparison.Ordinal))!;
			Has(game, "step", "stepName", "blocks", "text", "code", "means", "todo", "help");
			Check(game["blocks"]!.GetValue<bool>() && game["code"]!.ToString() == "TR-203" && game["todo"]!.ToString().Length > 10, "a stopping finding carries its code and what to do");
			var dry = JsonNode.Parse(Reports.Text(Reports.DryRun(DryRun.Make(new RunRequest(f.SetupFile, f.Data)), _ => TimeSpan.FromMinutes(3))))!;
			Has(dry, "wouldStart", "refusals", "notes", "preview");
			Check(!dry["wouldStart"]!.GetValue<bool>() && dry["refusals"]!.AsArray().Count == 1 && dry["preview"]!["steps"]![0]!["key"]!.ToString() == "landscapefixer" &&
				dry["preview"]!["steps"]![0]!["usualSeconds"]!.GetValue<double>() == 180, "a dry run: refused, with what it would do");
		} finally {
			Preflight.GameCheck = () => [];
		}
		var preview = JsonNode.Parse(Reports.Text(Reports.Preview(setup, RunPreview.Build(setup, mo2, setup.EnabledSteps), _ => null)))!;
		Has(preview, "steps", "offForTheRun", "replacedForGood", "startsTheGame", "closesMo2", "leftOutForNewGame", "commandsBefore", "commandsAfter", "disk", "sizeNotKnown");
		Check(preview["steps"]![0]!["usualSeconds"] == null && preview["offForTheRun"]!.AsArray().Any(m => m!["mod"]!.ToString() == "Output A"), "a preview");
		var tools = Reports.Tools([new ToolFinder.Row(new ToolFinder.Tool("dyndolod", "DynDOLOD", ["DynDOLODx64.exe"], "", "https://dyndolod.info", "its own folder"), ToolFinder.Found.Missing, null)], null);
		Check(tools["tools"]![0]!["state"]!.ToString() == "notFound" && tools["added"] == null, "tools");
		var updates = Reports.Updates(setup, [new ToolUpdates.Result("dyndolod", "DynDOLOD", "3.0", "3.1", true, "https://dyndolod.info", null)]);
		Check(updates["anyNewer"]!.GetValue<bool>() && updates["tools"]![0]!["newest"]!.ToString() == "3.1", "updates");
		var when = new DateTime(2026, 1, 2, 3, 4, 5);
		var changes = Reports.Changes(setup, when, 2, [new Mo2Changes.Change(when, "outside the runner", "A Mod", "ticked", ["Test"])]);
		Check(changes["watchedSince"]!.ToString() == "2026-01-02T03:04:05" && changes["newChanges"]!.GetValue<int>() == 2 && changes["changes"]![0]!["profiles"]![0]!.ToString() == "Test", "changes");
		var refused = Reports.Outcome(new RunOutcome(RunState.Failed, "not started: the game is running (SkyrimSE.exe) - close it first", null), null);
		Check(!refused["started"]!.GetValue<bool>() && refused["code"]!.ToString() == "TR-203" && refused["run"] == null, "a start that was refused");
		var ended = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		var outcome = JsonNode.Parse(Reports.Text(Reports.Outcome(ended, Journal.Load(ended.Folder!))))!;
		Has(outcome, "state", "started", "verdict", "run", "summary");
		Check(outcome["state"]!.ToString() == "finished" && outcome["run"]!["steps"]![0]!["state"]!.ToString() == "done" && outcome["summary"]!.AsArray().Any(x => x!["label"]!.ToString() == "Next"), "a run's end");
	}

	// the real program, started the way a script starts it: one JSON object on its output and an exit code - no window, nothing else
	static void T_TheProgramAnswersAScriptInJson()
	{
		Check(File.Exists(Path.Combine(AppContext.BaseDirectory, Brand.Name + ".exe")), "the program sits beside the tests");
		// a copy with a data folder of its own beside it (never under the temp folder: there its data would be elsewhere - AppPaths)
		var scratch = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!, "Tool Runner test json " + Guid.NewGuid().ToString("N"));
		try {
			foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)) {
				var rel = Path.GetRelativePath(AppContext.BaseDirectory, file);
				if (!rel.StartsWith(Brand.Name + " Data", StringComparison.OrdinalIgnoreCase) && !rel.StartsWith("logs", StringComparison.OrdinalIgnoreCase)) {
					Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(scratch, rel))!);
					File.Copy(file, Path.Combine(scratch, rel));
				}
			}
			// its own data folder beside it before it starts: an app-data folder this PC chose for every copy never takes it
			Directory.CreateDirectory(Path.Combine(scratch, Brand.Name + " Data"));
			(int Exit, string Out) Ask(params string[] a_args)
			{
				var psi = new ProcessStartInfo(Path.Combine(scratch, Brand.Name + ".exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = scratch };
				foreach (var a in a_args) {
					psi.ArgumentList.Add(a);
				}
				using var p = Process.Start(psi)!;
				using var endP = new StandIn(p);
				// read beside a wait that ends: read first and to its end, a child that hangs with its output open held this
				// test - and every test after it - for good
				var reading = p.StandardOutput.ReadToEndAsync();
				if (!p.WaitForExit(90_000) || !reading.Wait(TimeSpan.FromSeconds(30))) {
					throw new InvalidOperationException("it was still running, or its output still open, after 90 s: " + string.Join(" ", a_args));
				}
				return (p.ExitCode, reading.Result);
			}
			var (exit, text) = Ask("status", "--json");
			var o = JsonNode.Parse(text)!;
			Check(exit == 0 && o["command"]!.ToString() == "status" && o["state"]!.ToString() == "idle" && o["safeToClose"]!.GetValue<bool>(), $"status as JSON (exit {exit}): {text}");
			(exit, text) = Ask("status");
			Check(exit == 0 && text.Contains("idle: no run is going", StringComparison.Ordinal) && text.Contains("safe to close", StringComparison.Ordinal), $"status in words (exit {exit}): {text}");
			// a command that cannot be carried out still answers a script in JSON
			(exit, text) = Ask("dryrun", "--setup", Path.Combine(scratch, "no such setup.json"), "--json");
			var refused = JsonNode.Parse(text)!;
			Check(exit == 1 && !refused["wouldStart"]!.GetValue<bool>() && refused["refusals"]![0]!["code"]!.ToString() == "TR-102", $"a dry run of a setup that is not there (exit {exit}): {text}");
			(exit, text) = Ask("preview", "--json");
			Check(exit == 3 && JsonNode.Parse(text)!["error"]!.ToString().Contains("--setup", StringComparison.Ordinal), $"a command with an argument missing (exit {exit}): {text}");
		} finally {
			for (var i = 0; i < 20 && Directory.Exists(scratch); i++) {
				try {
					Directory.Delete(scratch, true);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Thread.Sleep(250);
				}
			}
		}
	}

	// the window's answer to "may I close this now?", and its own work it is never closed in the middle of
	static void T_TheWindowSaysWhenItIsSafeToClose()
	{
		Equal(("good", "Safe to close"), ToolRunner.App.MainViewModel.CloseState(false, false, ""), "nothing going");
		var run = ToolRunner.App.MainViewModel.CloseState(true, false, "");
		Check(run.Kind == "warn" && run.Text == "Closing stops the run", "a run going: closing stops it cleanly");
		Check(ToolRunner.App.MainViewModel.CloseState(false, true, "").Text.Contains("starting", StringComparison.Ordinal), "a run starting");
		Equal(("bad", "Do not close yet: your profile is being put back"), ToolRunner.App.MainViewModel.CloseState(true, true, "your profile is being put back"), "the window's own work comes first");
		var work = new ToolRunner.App.WorkInHand();
		int changed = 0, freed = 0;
		work.Changed += () => ++changed;
		work.Freed += () => ++freed;
		Equal("", work.Now, "nothing in hand");
		var putBack = work.Begin("your profile is being put back");
		var learn = work.Begin("DynDOLOD is open to learn its settings");
		Equal("DynDOLOD is open to learn its settings", work.Now, "the newest is named");
		learn.Dispose();
		learn.Dispose();
		Check(work.Now == "your profile is being put back" && freed == 0, "one ended (let go twice changes nothing): the other is still in hand");
		putBack.Dispose();
		Check(work.Now == "" && freed == 1 && changed == 4, $"the last one ended: free, said once ({freed}, {changed})");
	}

	// the first start, step by step: what is done, what comes next
	static void T_TheFirstStartIsGuidedStepByStep()
	{
		var done = ToolRunner.App.Theme.Glyph("Done");
		var none = ToolRunner.App.MainViewModel.FirstSteps(false, false, false);
		Check(none.Count == 4 && none.Select(r => r.Mark).SequenceEqual(["1", "2", "3", "4"]) && none[0].Kind == "" && none.Skip(1).All(r => r.Kind == "muted"), "no setup yet: the first step is next");
		Check(none[0].Text.Contains("New setup", StringComparison.Ordinal) && none[2].Text.Contains("Nothing is started", StringComparison.Ordinal), "each says what to press and what it does");
		var made = ToolRunner.App.MainViewModel.FirstSteps(true, false, false);
		Check(made[0].Kind == "good" && made[0].Mark == done && made[1].Kind == "" && made[2].Kind == "muted", "a setup made: the check is next");
		var checkedClean = ToolRunner.App.MainViewModel.FirstSteps(true, true, false);
		Check(checkedClean[1].Mark == done && checkedClean[2].Kind == "" && checkedClean[3].Kind == "muted", "checked: the dry run is next");
		var dried = ToolRunner.App.MainViewModel.FirstSteps(true, true, true);
		Check(dried.Take(3).All(r => r.Kind == "good") && dried[3].Kind == "" && dried[3].Mark == "4", "all but the start done");
	}

	// No wait of the engine longer than a moment may be a plain sleep: a step waits with RunContext.Pause / Waits.Pause, which a
	// Stop ends at once
	static void T_NoStepWaitIsBlindToStop() => NoBlindSleeps(SourceDir());

	static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string a_here = "") => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(a_here)!)!, "Runner.Core");

	static void NoBlindSleeps(string a_core)
	{
		if (!Directory.Exists(a_core)) {
			Console.WriteLine($"  (skipped: the source is not at {a_core})");
			return;
		}
		// "Again[i]": the file layer's own tries when another program holds a file for a moment (about two seconds in all)
		var allowed = new HashSet<string> { "Poll", "i == 0 ? 0 : 200", "Again[i]" };
		var found = new List<string>();
		foreach (var file in Directory.EnumerateFiles(a_core, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains(@"\obj\", StringComparison.Ordinal) && !p.Contains(@"\bin\", StringComparison.Ordinal))) {
			var lines = File.ReadAllLines(file);
			for (var i = 0; i < lines.Length; ++i) {
				var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"Thread\.Sleep\(([^;]*)\);");
				if (!m.Success) {
					continue;
				}
				var arg = m.Groups[1].Value.Trim();
				if (!allowed.Contains(arg) && !(int.TryParse(arg, out var ms) && ms <= 1500)) {
					found.Add($"{Path.GetFileName(file)}:{i + 1} Thread.Sleep({arg})");
				}
			}
		}
		Check(found.Count == 0, "a wait a Stop cannot end - use a_ctx.Pause(...) or Waits.Pause(..., a_ct): " + string.Join("; ", found));
	}

	// ... and the guard is a real one: a planted long sleep is caught, a pause and a short poll are not
	static void T_ABlindSleepIsCaught()
	{
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "sleeps " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		try {
			File.WriteAllText(Path.Combine(dir, "Good.cs"), "a_ctx.Pause(TimeSpan.FromSeconds(30));\nThread.Sleep(500);\nThread.Sleep(Poll);\n");
			NoBlindSleeps(dir);
			File.WriteAllText(Path.Combine(dir, "Bad.cs"), "Thread.Sleep(TimeSpan.FromSeconds(a_s.Int(\"settle_s\", 30)));\n");
			string? caught = null;
			try {
				NoBlindSleeps(dir);
			} catch (InvalidOperationException e) {
				caught = e.Message;
			}
			Check(caught != null && caught.Contains("Bad.cs:1", StringComparison.Ordinal), "the planted sleep is caught and named: " + caught);
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// A cache built for two ticked shader mods together is stale when either changes; a cache with no layers reads as before
	static void T_ShaderLayersArePartOfTheirCache()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		foreach (var m in new[] { "Layer", "Under" }) {
			Directory.CreateDirectory(Path.Combine(f.Mods, m, "SKSE", "Plugins"));
			File.WriteAllText(Path.Combine(f.Mods, m, "SKSE", "Plugins", "CommunityShaders.dll"), m + " 1");
			Directory.CreateDirectory(Path.Combine(f.Mods, m, "Shaders"));
			File.WriteAllText(Path.Combine(f.Mods, m, "Shaders", "Utility.hlsl"), m);
		}
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Layer\r\n+Under\r\n" + f.Text("modlist.txt"));
		var p = ProfileOf(f);
		var c = new ShaderStep.Cache("Layer", "Cache", null);
		var under = new ShaderStep.Cache("Under", "Cache - Under", null);
		var alone = ShaderStep.Fingerprint(mo2, c, p, ShaderStep.OtherCacheMods([c, under], c));
		Equal(alone, ShaderStep.Fingerprint(mo2, c, p, ShaderStep.OtherCacheMods([c, under], c), []), "no layers: the fingerprint an older stamp holds");
		List<string> none = ["Cache - Under"];
		var both = ShaderStep.Fingerprint(mo2, c, p, none, ["Under"]);
		Check(both != alone, "built together with the shader mod under it: another cache");
		File.WriteAllText(Path.Combine(f.Mods, "Under", "SKSE", "Plugins", "CommunityShaders.dll"), "Under 2");
		Check(both != ShaderStep.Fingerprint(mo2, c, p, none, ["Under"]), "the layer under it updated: the cache is stale");
		Equal(alone, ShaderStep.Fingerprint(mo2, c, p, ShaderStep.OtherCacheMods([c, under], c)), "while a cache that does not include it is untouched");
	}

	// an older output of the same tool still ticked would win over the fresh one (a new output sits at the bottom
	// of the left pane) - named before the run; once the step replaces it (kept off by the run) it is no longer named
	static void T_ASecondOutputOfTheSameToolIsNamed()
	{
		using var f = new FakeMo2();
		f.Mod("Old DynDOLOD");
		File.WriteAllText(Path.Combine(f.Mods, "Old DynDOLOD", "DynDOLOD.esp"), "x");
		f.Mod("Old Grass");
		Directory.CreateDirectory(Path.Combine(f.Mods, "Old Grass", "grass"));
		File.WriteAllText(Path.Combine(f.Mods, "Old Grass", "grass", "Tamrielx0000y0000.cgid"), "x");
		f.Mod("Unticked DynDOLOD");
		File.WriteAllText(Path.Combine(f.Mods, "Unticked DynDOLOD", "DynDOLOD.esm"), "x");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Old DynDOLOD\r\n+Old Grass\r\n-Unticked DynDOLOD\r\n" + f.Text("modlist.txt"));
		var setup = f.WriteSetup(("dyndolod", new JsonObject { ["output_mod"] = "DynDOLOD Output" }), ("grass", new JsonObject { ["output_mod"] = "Grass Out" }));
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		var states = ProfileOf(f).States();
		var dyn = Duplicates.Check(setup, mo2, states, "dyndolod").ToList();
		Equal(1, dyn.Count, "one rival: " + string.Join(" | ", dyn.Select(i => i.Text)));
		Check(dyn[0].Text.Contains("Old DynDOLOD", StringComparison.Ordinal) && dyn[0].Text.Contains("DynDOLOD.esp", StringComparison.Ordinal) && !dyn[0].Blocking, "named with its file, as a note");
		Check(Duplicates.Check(setup, mo2, states, "grass").Any(i => i.Text.Contains("Old Grass", StringComparison.Ordinal)), "a grass cache of its kind counts too");
		// the step replaces it: the run keeps it off, so it is no rival
		var replaced = f.WriteSetup(("dyndolod", new JsonObject { ["output_mod"] = "DynDOLOD Output", ["replaces"] = new JsonArray("Old DynDOLOD") }));
		Equal(0, Duplicates.Check(replaced, mo2, states, "dyndolod").Count(), "a replaced mod is not named");
		Equal<string?>(null, Duplicates.Holds(Path.Combine(f.Mods, "Old DynDOLOD"), "pandora"), "a step with no own file is never guessed");
	}

	// the window knows a run is alive by its engine's process name: after a rename it knew only the old names, so every real run
	// read as dead - Stop now, Stop after, a guided step's Done and the close guard did nothing. The tests' own process
	// matched, which hid it: here the real names are asked, and the program's name is held
	// to its exe's name so a future rename cannot split them again
	static void T_TheEngineIsKnownByItsRealName()
	{
		foreach (var n in new[] { "Tool Runner", "output manager", "skyrim utility patcher", "MO2 Tool Runner", "Patcher Unification Project", "Runner.Tests" }) {
			Check(Journal.IsRunnerName(n), $"\"{n}\" is the program");
		}
		foreach (var n in new[] { "ModOrganizer", "Tool Runner Helper", "Another Tool Runner", "Runner", "SkyrimSE", "" }) {
			Check(!Journal.IsRunnerName(n), $"\"{n}\" is not the program");
		}
		Equal(Brand.Name, typeof(ToolRunner.App.App).Assembly.GetName().Name, "the program's name is its exe's name");
		Check(Journal.IsRunner(Environment.ProcessId), "this process (the tests) counts as the program");
	}

	// every look at the process list by name is one walk of it: found by an image's name in any case and with or without ".exe", a
	// process's own name read back, several names answered at once - and a process that ended is gone though its handle is still held
	static void T_AProcessIsFoundByItsImageInOneLook()
	{
		using var ping = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
		try {
			Check(Procs.Pids("ping.exe").Contains(ping.Id) && Procs.Pids("PING").Contains(ping.Id), "its id, by its image in any case, with or without .exe");
			Check(Procs.Running("Ping.exe") && !Procs.Running("no such program 7f3a.exe") && !Procs.Running("pin.exe"), "running or not, by the whole name");
			var both = Procs.Running(["no such program 7f3a.exe", "PING.EXE"]);
			Check(both is [false, true], "several images answered from one look");
			Check(string.Equals("PING.EXE", Procs.Image(ping.Id), StringComparison.OrdinalIgnoreCase) && string.Equals("PING", Procs.Name(ping.Id), StringComparison.OrdinalIgnoreCase),
				"a process's own image and its name");
			var found = Procs.ByImage("ping.exe");
			Check(found.Any(p => p.Id == ping.Id), "the process itself for a caller that needs more than its id");
			found.ForEach(p => p.Dispose());
			Check(Procs.Snapshot().Contains((ping.Id, Environment.ProcessId)), "its parent is this process");
			Check(Procs.Tree(Environment.ProcessId).Contains(ping.Id), "so it is in this process's tree");
		} finally {
			ping.Kill();
			ping.WaitForExit();
		}
		Equal("", Procs.Image(ping.Id), "ended: no image, though this test still holds its handle");
		Check(Procs.Name(ping.Id) == null && !Procs.Pids("ping.exe").Contains(ping.Id), "and no name, and not in the list");
	}

	// the window follows the runs folder: a run record, the queue or the notice before the PC sleeps written there wakes its look at
	// once (so it may look seldom while nothing runs); a log line or a new folder does not
	static void T_AChangedRunRecordWakesTheWindow()
	{
		var followed = ToolRunner.App.RunsWatch.Followed;
		Check(followed.Contains("journal.json") && followed.Contains(Path.GetFileName(QueueRun.File("x"))) && followed.Contains(Path.GetFileName(QueueRun.LastFile("x"))) &&
		      followed.Contains(Path.GetFileName(ToolRunner.App.Engine.RefusalFile)), "every file the window's look reads is followed");
		var dir = Path.Combine(Path.GetTempPath(), "Tool Runner tests", "runs watch " + Guid.NewGuid().ToString("N")[..8]);
		try {
			using var woke = new ManualResetEventSlim();
			using var watch = new ToolRunner.App.RunsWatch(dir, woke.Set);
			Check(watch.Watching, "the runs folder is watched (and made when it was not there)");
			var run = Path.Combine(dir, "2026-11-10 21-00-00 My Profile");
			Directory.CreateDirectory(run);
			File.WriteAllText(Path.Combine(run, "log.txt"), "a line\n");
			Check(!woke.Wait(400), "a new run folder and a log line wake nothing");
			Files.WriteAtomic(Path.Combine(run, "journal.json"), "{}");
			Check(woke.Wait(5000), "a run record written wakes it");
			Thread.Sleep(300);
			woke.Reset();
			Files.WriteAtomic(Path.Combine(dir, "queue.json"), "{}");
			Check(woke.Wait(5000), "the queue's file too");
			Thread.Sleep(300);
			woke.Reset();
			File.Delete(Path.Combine(run, "journal.json"));
			Check(woke.Wait(5000), "and a record that goes");
		} finally {
			Directory.Delete(dir, true);
		}
		// a folder that cannot be made or watched leaves no watcher and no error: the timer alone looks
		var file = Path.GetTempFileName();
		try {
			using var none = new ToolRunner.App.RunsWatch(Path.Combine(file, "Runs"), () => { });
			Check(!none.Watching, "nowhere to watch: no watcher, nothing thrown");
		} finally {
			File.Delete(file);
		}
	}

	// a rename: what a player or a modder made under a name the program had before is still the program's own - an output mod's mark, an
	// add-on's folder, the ProgramData folders, and the Tools menu entry, which never shows twice. The name now is the tail of the oldest
	// one, so each kind is also asked with that old name and with a near miss
	static void T_WhatAnOlderNameMadeIsStillItsOwn()
	{
		Equal("Output Manager", Brand.OldNames[0], "the name it had last is the first old one");
		Check(Brand.Is("skyrim utility patcher") && Brand.Is("Tool Runner") && Brand.Is("output manager") && Brand.Is("MO2 Tool Runner") && !Brand.Is("Tool Runner 2") &&
		      !Brand.Is("Runner") && !Brand.Is("My Tool Runner") && !Brand.Is(null), "a name of its own, whole, any case");
		Check(Brand.Made("Skyrim Utility Patcher - ready to play") && Brand.Made("Output Manager - DynDOLOD off") && Brand.Made("Tool Runner - clean save") &&
		      Brand.Made("MO2 Tool Runner - clean save") && !Brand.Made("Another Tool Runner - clean save") && !Brand.Made("Tool Runners - clean save"), "saves under any of its names are its own, a near miss is not");
		Check(Profile.MadeByUs("[General]\r\ncomments=made by Skyrim Utility Patcher\r\n") && Profile.MadeByUs("comments=made by Tool Runner") &&
		      Profile.MadeByUs("comments=made by Output Manager") && Profile.MadeByUs("comments=made by MO2 Tool Runner") &&
		      !Profile.MadeByUs("comments=made by DynDOLOD"), "an output mod made under an old name is known as its own");
		Check(Schedule.IsOurs("MO2 Tool Runner - Bottled Shaders 1A2B3C") && Schedule.IsOurs("\\Output Manager - Bottled Shaders 1A2B3C") && !Schedule.IsOurs("Some Tool Runner - x"),
			"a task an old name left is still its own to clear away");
		Check(!Brand.Compact.Contains(' ', StringComparison.Ordinal) && Brand.OldCompact.Contains("SkyrimUtilityPatcher") && Brand.OldCompact.Contains("OutputManager") &&
		      Brand.OldWorkRoots.Any(r => r.EndsWith("\\SkyrimUtilityPatcher", StringComparison.Ordinal)) && Brand.OldWorkRoots.Any(r => r.EndsWith("\\MO2ToolRunner", StringComparison.Ordinal)) &&
		      Brand.WorkRoot.EndsWith("\\ToolRunner", StringComparison.Ordinal) && !Brand.OldWorkRoots.Contains(Brand.WorkRoot), "its ProgramData folder, and the old ones the clean removal still takes");
		Check(DataRemoval.WorkRoots.Count() == 1 + Brand.OldCompact.Length, "the clean removal looks in every one of them");

		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		// an add-on a modder shipped for the old name: found in its old folder
		var mod = Path.Combine(f.Mods, "Base Mod");
		Directory.CreateDirectory(Path.Combine(mod, "Skyrim Utility Patcher"));
		Directory.CreateDirectory(Path.Combine(mod, "tools"));
		File.WriteAllText(Path.Combine(mod, "tools", "MyPatcher.exe"), "");
		File.WriteAllText(Path.Combine(mod, "Skyrim Utility Patcher", "patcher.json"),
			"""{ "schema": 1, "name": "My Patcher", "author": "A Modder", "page": "https://example.com/p", "program": "tools/MyPatcher.exe", "arguments": "--auto", "output": "My Patcher Output", "minutes": 30, "makes": "MyPatch.esp", "after": "synthesis" }""");
		var found = AddOns.Find(mo2, ProfileOf(f));
		Check(found.Count == 1 && found[0].Problems.Count == 0 && found[0].AddOn?.Name == "My Patcher", "an add-on in the old name's folder is found: " + string.Join("; ", found.SelectMany(x => x.Problems)));
		Check(AddOns.IsFolderInMod("Skyrim Utility Patcher") && AddOns.IsFolderInMod(AddOns.FolderInMod) && !AddOns.IsFolderInMod("tools"), "and its folder is known by either name");

		// the Tools menu entry: the old-named file leaves when the new one is added; another author's file of that name stays
		const string Program = @"D:\Tools\Tool Runner.exe";
		var plugins = Path.Combine(Path.GetDirectoryName(f.Exe)!, "plugins");
		Directory.CreateDirectory(plugins);
		var old = Path.Combine(plugins, "skyrimutilitypatcher.py");
		var older = Path.Combine(plugins, "mo2toolrunner.py");
		var last = Path.Combine(plugins, "outputmanager.py");
		File.WriteAllText(old, Mo2MenuEntry.Text(@"D:\Tools\Skyrim Utility Patcher.exe"));
		File.WriteAllText(older, Mo2MenuEntry.Text(@"D:\Tools\MO2 Tool Runner.exe"));
		File.WriteAllText(last, Mo2MenuEntry.Text(@"D:\Tools\Output Manager.exe"));
		Check(!Mo2MenuEntry.Present(mo2) && Mo2MenuEntry.OldOnes(mo2).Order().SequenceEqual(new[] { old, older, last }.Order()), "only the old names' entries are there: none is this version's");
		Mo2MenuEntry.Add(mo2, Program);
		Check(Mo2MenuEntry.IsCurrent(mo2, Program) && !File.Exists(old) && !File.Exists(older) && !File.Exists(last), "the new entry is in and the old ones gone - the program is in the menu once");
		File.WriteAllText(old, "# someone else's plugin that happens to have this name\n");
		Check(!Mo2MenuEntry.OldOnes(mo2).Any() && Mo2MenuEntry.IsCurrent(mo2, Program), "a file of that name that is not the entry is not touched");
		Mo2MenuEntry.Add(mo2, Program);
		Check(File.Exists(old), "and stays");
	}

	// a setup keeps its newest 100 run records; an unfinished one and the running one are never removed, another setup's never
	static void T_OldRunRecordsAreTrimmed()
	{
		using var f = new FakeMo2();
		var runs = Runner.RunsDir(f.Data);
		Journal Make(int a_day, int a_minute, string a_setup, RunState a_state)
		{
			var id = $"2026-{1 + a_day / 28:00}-{1 + a_day % 28:00} 10-{a_minute:00}-00 x";
			var j = new Journal { Id = id, Folder = Path.Combine(runs, id), SetupFile = a_setup, SetupName = "x", Started = DateTime.Now, State = a_state };
			Directory.CreateDirectory(j.Folder);
			j.Save();
			return j;
		}
		var oldestUnfinished = Make(0, 0, f.SetupFile, RunState.Running);
		for (var i = 1; i <= 104; ++i) {
			Make(i, 0, f.SetupFile, RunState.Finished);
		}
		var other = Make(0, 30, @"C:\Other.json", RunState.Finished);
		var removed = Runner.PruneRunRecords(runs, f.SetupFile, null, 100, RunLog.Null);
		Equal(4, removed.Count, "105 records of this setup, 100 kept, the unfinished one kept too");
		Check(Directory.Exists(oldestUnfinished.Folder), "the run that did not finish stays (its put-back needs it)");
		Check(Directory.Exists(other.Folder), "another setup's record stays");
		Equal(101, Journal.All(runs).Count(j => j.SetupFile == f.SetupFile), "100 + the unfinished one");
		// a record that does not read may be one of this setup's newest: which are the newest is not known, so none goes
		var damaged = Path.Combine(runs, "2026-12-30 10-00-00 x");
		Directory.CreateDirectory(damaged);
		File.WriteAllText(Path.Combine(damaged, "journal.json"), "{ not a record");
		Equal(0, Runner.PruneRunRecords(runs, f.SetupFile, null, 50, RunLog.Null).Count, "nothing removed while a record does not read");
		Equal(101, Journal.All(runs).Count(j => j.SetupFile == f.SetupFile), "all of them still there");
	}

	// a PC low on memory is told before a run, never stopped (the LOD tools may still manage)
	static void T_LowMemoryIsToldNotBlocked()
	{
		var small = SystemChecks.Memory((6, 3)).ToList();
		Check(small.Count == 1 && !small[0].Blocking && small[0].Text.Contains("6 GB", StringComparison.Ordinal), "a 6 GB PC is told: " + string.Join(" | ", small.Select(i => i.Text)));
		var busy = SystemChecks.Memory((32, 1.5)).ToList();
		Check(busy.Count == 1 && !busy[0].Blocking && busy[0].Text.Contains("free", StringComparison.Ordinal), "little free memory is told");
		Equal(0, SystemChecks.Memory((32, 16)).Count(), "a roomy PC hears nothing");
		Equal(0, SystemChecks.Memory(null).Count(), "no reading, no note");
		Check(SystemChecks.MemoryProbe() is { } m && m.Total > 0, "the stand-in reads (the real probe is Windows' own)");
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	static extern uint GetGuiResources(nint a_process, uint a_flags);

	// what this process holds: the managed bytes that survive a full, compacting collection (GC.GetTotalMemory read negative on .NET
	// 10 here - the collector's own record of what it kept is used instead), kernel handles, GDI and USER objects
	static (long Managed, int Handles, uint Gdi, uint User) Held()
	{
		GC.Collect(2, GCCollectionMode.Forced, true, true);
		GC.WaitForPendingFinalizers();
		GC.Collect(2, GCCollectionMode.Forced, true, true);
		var kept = GC.GetGCMemoryInfo(GCKind.FullBlocking).PromotedBytes;
		using var me = System.Diagnostics.Process.GetCurrentProcess();
		me.Refresh();
		return (kept, me.HandleCount, GetGuiResources(me.Handle, 0), GetGuiResources(me.Handle, 1));
	}

	// every hot path a long run and the open window loop over (the process looks, the window and
	// box reads through UI Automation, the run records, a process tree's CPU, the run's command watcher) run thousands of times;
	// what the process holds afterwards must come back to where it started - a leak of a handle, a GDI / USER object or managed
	// memory shows here as growth that a collection does not take back. Set ToolRunnerSoak=<rounds> for a longer soak.
	static void T_HotPathsLeakNothing()
	{
		var rounds = int.TryParse(Environment.GetEnvironmentVariable("ToolRunnerSoak"), out var r) && r > 0 ? r : 600;
		using var f = new FakeMo2();
		var runs = Runner.RunsDir(f.Data);
		for (var i = 0; i < 40; ++i) {
			var id = $"2026-11-{1 + i % 28:00} 1{i % 10}-00-00 soak";
			var j = new Journal { Id = id, Folder = Path.Combine(runs, id), SetupFile = f.SetupFile, SetupName = "soak", Started = DateTime.Now, State = RunState.Finished,
				Steps = [new StepRecord { Key = "dyndolod", State = StepState.Done }] };
			Directory.CreateDirectory(j.Folder);
			j.Save();
		}
		var cache = new JournalCache(runs);
		var explorer = Procs.Pids("explorer.exe");
		var me = Environment.ProcessId;
		// each hot path on its own, so a growth names its path
		var parts = new (string Name, int Every, Action Run)[] {
			("process looks", 1, () => {
				_ = Procs.Running("ModOrganizer.exe");
				_ = Mo2Control.GameImages.Any(Procs.Running);
				_ = Procs.Pids("explorer.exe");
				_ = StepKit.PidsOf("svchost.exe")();
			}),
			("run records", 1, () => _ = cache.Read()),
			("process tree CPU", 1, () => {
				_ = Procs.TreeCpu(me);
				_ = Procs.StartTime(me);
				_ = Journal.IsRunner(me);
			}),
			("window list", 1, () => _ = Native.TopWindowsOf(explorer)),
			("UI Automation boxes", 1, () => _ = Uia.Boxes(explorer)),
		};
		// every path warmed up first: UI Automation's first use starts its own threads and connections (about 100 handles, once)
		foreach (var (_, every, run) in parts) {
			for (var i = 0; i < 100; i += every) {
				run();
			}
		}
		// Explorer's own dialogs that stand open on this PC right now (a Properties window, a copy in progress). Each look
		// makes UI Automation elements for those - that is what the look is for - and .NET's client keeps a little of every
		// element it makes (measured: about 10 KB a look for one Properties window, 6 MB over this soak, where the
		// same code kept nothing an hour earlier with no dialog open). They are the PC's state, not the program's: the
		// path's allowance grows with them, so the test judges the same thing on every PC - and still fails when elements
		// are made for windows that are no boxes (that keeps tens of megabytes over the soak).
		int BoxWindows() => Native.TopWindowsOf(explorer).Count(h => Uia.BoxClass(Native.ClassName(h)) || Native.ClassName(h).StartsWith("Qt", StringComparison.Ordinal));
		var grew = new List<string>();
		var before = Held();
		foreach (var (name, every, run) in parts) {
			// counted at each look of the path that reads them, and added up: the allowance is for the dialogs that stood
			// open when a look ran - never for one that came up after the last look, or stood there for a part of them only
			var readsDialogs = name == "UI Automation boxes";
			long dialogLooks = 0;
			var p0 = Held();
			var marks = new List<string>();
			for (var i = 1; i <= rounds; i += every) {
				if (readsDialogs) {
					dialogLooks += BoxWindows();
				}
				run();
				if (i % Math.Max(1, rounds / 3) == 0) {
					var m = Held();
					marks.Add($"{m.Managed / 1024} KB / {m.Handles} handles at {i}");
				}
			}
			var p1 = Held();
			Console.WriteLine($"      {name}: {rounds / every} calls, managed {p0.Managed / 1024} -> {p1.Managed / 1024} KB, handles {p0.Handles} -> {p1.Handles}  ({string.Join(", ", marks)})");
			if (dialogLooks > 0) {
				Console.WriteLine($"      (dialogs of Explorer's own stood open at its looks, {dialogLooks} dialog-look(s) in all: its allowance grows by 16 KB for each)");
			}
			// a growth is a leak when it goes on to the end: a cache fills and stops, so the last third must not grow like the first
			if (p1.Managed - p0.Managed > 1024 * 1024 + 16L * 1024 * dialogLooks || p1.Handles - p0.Handles > 20) {
				grew.Add($"{name} grew {(p1.Managed - p0.Managed) / 1024} KB and {p1.Handles - p0.Handles} handles");
			}
		}
		// the run's command watcher made and let go of, as every run does
		for (var i = 0; i < 50; ++i) {
			using var c = new Commands(new Journal { Folder = f.Data }, () => { });
			c.Start();
		}
		var after = Held();
		// for the record (not judged - it is .NET's own client; measured after the judged part, since its first use starts its own
		// threads): what UI Automation elements cost when made and dropped - the reason Uia.Boxes makes one only for a window that can
		// be a box, and the reason a whole window's text is read seldom
		var windows = Native.TopWindowsOf(explorer);
		if (windows.Count > 0) {
			_ = Uia.FromHandle(windows[0]);
			var u0 = Held();
			for (var i = 0; i < 300; ++i) {
				_ = Uia.FromHandle(windows[i % windows.Count]);
			}
			var u1 = Held();
			var tray = windows.Select(Uia.FromHandle).OfType<System.Windows.Automation.AutomationElement>().OrderByDescending(e => Uia.All(e).Count).First();
			var n = Uia.All(tray).Count;
			var t0 = Held();
			for (var i = 0; i < 30; ++i) {
				_ = Uia.TextOf(tray, int.MaxValue);
			}
			var t1 = Held();
			Console.WriteLine($"      (.NET's UI Automation client: 300 elements made and dropped kept {(u1.Managed - u0.Managed) / 1024} KB; a window's whole text " +
				$"({n} elements) read 30 times kept {(t1.Managed - t0.Managed) / 1024} KB)");
		}
		Console.WriteLine($"      soak of {rounds} rounds: managed {before.Managed} -> {after.Managed} bytes, handles {before.Handles} -> {after.Handles}, " +
			$"GDI {before.Gdi} -> {after.Gdi}, USER {before.User} -> {after.User}");
		Check(grew.Count == 0, "no hot path keeps memory or handles: " + string.Join("; ", grew));
		Check(after.Handles - before.Handles < 40, $"kernel handles come back (grew {after.Handles - before.Handles})");
		Check(after.Gdi <= before.Gdi + 5, $"GDI objects come back (grew {(int)after.Gdi - (int)before.Gdi})");
		Check(after.User <= before.User + 5, $"USER objects come back (grew {(int)after.User - (int)before.User})");
	}
}
