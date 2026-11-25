using System.Text;

namespace ToolRunner;

// Plugin headers: the master list (so switching a mod off never leaves a plugin without its master - a start-up crash) and
// the master flag (where a new master-flagged plugin goes in the load order).
public static class Plugins
{
	static readonly string[] Ext = [".esp", ".esm", ".esl"];

	public static bool IsPlugin(string a_name) => Ext.Any(e => a_name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

	public static List<string> InMod(string a_modDir)
	{
		try {
			return Directory.Exists(a_modDir) ? Directory.GetFiles(a_modDir).Select(Path.GetFileName).OfType<string>().Where(IsPlugin).ToList() : [];
		} catch (IOException) {
			return [];
		}
	}

	// The plugins a plugin needs. A file that is no plugin (it does not begin as one) needs none. One that begins as a plugin
	// and ends before its header is whole, or whose header does not add up, throws an IOException: its masters are not known,
	// and it is never taken for a plugin that needs none
	public static List<string> Masters(string a_path)
	{
		var outList = new List<string>();
		using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		var head = new byte[24];
		// one Read may stop short of what was asked (a network drive): read until the header is whole or the file ends
		var got = fs.ReadAtLeast(head, 24, false);
		if (got < 4 || Encoding.ASCII.GetString(head, 0, 4) != "TES4") {
			return outList;
		}
		var name = Path.GetFileName(a_path);
		if (got < 24) {
			throw new EndOfStreamException($"{name} ends inside its header");
		}
		var size = BitConverter.ToUInt32(head, 4);
		if (size > 16 * 1024 * 1024) {
			throw new IOException($"{name}'s header says it is {size:#,0} bytes long, more than a plugin's header can be");
		}
		var data = new byte[size];
		try {
			fs.ReadExactly(data);
		} catch (EndOfStreamException e) {
			throw new EndOfStreamException($"{name} ends inside its header", e);
		}
		// a field over 65,535 bytes long (the list of overridden records in a big master) has its length in an XXXX field before it
		uint big = 0;
		for (var i = 0; i + 6 <= data.Length;) {
			var type = Encoding.ASCII.GetString(data, i, 4);
			long len = big != 0 ? big : BitConverter.ToUInt16(data, i + 4);
			big = 0;
			if (i + 6 + len > data.Length) {
				throw new IOException($"{name}'s header does not add up (a field of it runs past its end)");
			}
			if (type == "XXXX" && len == 4) {
				big = BitConverter.ToUInt32(data, i + 6);
			} else if (type == "MAST") {
				var body = data.AsSpan(i + 6, (int)len);
				var z = body.IndexOf((byte)0);
				outList.Add(Encoding.Latin1.GetString(z >= 0 ? body[..z] : body));
			}
			i += 6 + (int)len;
		}
		return outList;
	}

	// the record types a plugin holds: the labels of its top-level groups, read off their headers only (the file is not parsed)
	public static HashSet<string> TopGroups(string a_path)
	{
		var outSet = new HashSet<string>(StringComparer.Ordinal);
		using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		var head = new byte[24];
		if (fs.ReadAtLeast(head, 24, false) < 24 || Encoding.ASCII.GetString(head, 0, 4) != "TES4") {
			return outSet;
		}
		fs.Seek(BitConverter.ToUInt32(head, 4), SeekOrigin.Current);
		while (fs.Position + 24 <= fs.Length && fs.ReadAtLeast(head, 24, false) == 24) {
			var size = BitConverter.ToUInt32(head, 4);
			if (Encoding.ASCII.GetString(head, 0, 4) != "GRUP" || size < 24) {
				break;
			}
			outSet.Add(Encoding.ASCII.GetString(head, 8, 4));
			fs.Seek(size - 24, SeekOrigin.Current);
		}
		return outSet;
	}

	// what a plugin holds, read off record and group HEADERS only (nothing decompressed): its top-level group labels, "TREE" also
	// for a STAT record flagged "Has Tree LOD" (flag 0x40), and with a_land "LAND" when a worldspace group holds a landscape record
	public static HashSet<string> Kinds(string a_path, bool a_land)
	{
		var outSet = new HashSet<string>(StringComparer.Ordinal);
		using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		var head = new byte[24];
		if (fs.ReadAtLeast(head, 24, false) < 24 || Encoding.ASCII.GetString(head, 0, 4) != "TES4") {
			return outSet;
		}
		fs.Seek(BitConverter.ToUInt32(head, 4), SeekOrigin.Current);
		while (fs.Position + 24 <= fs.Length && fs.ReadAtLeast(head, 24, false) == 24) {
			var size = BitConverter.ToUInt32(head, 4);
			if (Encoding.ASCII.GetString(head, 0, 4) != "GRUP" || size < 24) {
				break;
			}
			var label = Encoding.ASCII.GetString(head, 8, 4);
			outSet.Add(label);
			var end = fs.Position - 24 + size;
			if ((label == "STAT" && !outSet.Contains("TREE")) || (label == "WRLD" && a_land && !outSet.Contains("LAND"))) {
				// inside the group: a nested group's header is stepped into, a record is skipped by its data size
				while (fs.Position + 24 <= end && fs.ReadAtLeast(head, 24, false) == 24) {
					var type = Encoding.ASCII.GetString(head, 0, 4);
					if (type == "GRUP") {
						continue;
					}
					if (label == "STAT" && (BitConverter.ToUInt32(head, 8) & 0x40) != 0) {
						outSet.Add("TREE");
						break;
					}
					if (label == "WRLD" && type == "LAND") {
						outSet.Add("LAND");
						break;
					}
					fs.Seek(BitConverter.ToUInt32(head, 4), SeekOrigin.Current);
				}
			}
			fs.Seek(end, SeekOrigin.Begin);
		}
		return outSet;
	}

	// a light plugin takes no full load-order slot: an .esl file, or an .esp / .esm whose TES4 record carries the light flag (0x200)
	public static bool IsLight(string a_path) => LightOrNot(a_path) == true;

	// light or full; null when the file cannot be read (it is neither said to be light nor counted as full)
	public static bool? LightOrNot(string a_path) =>
		a_path.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) ? true : HeaderFlags(a_path) is { } f ? (f & 0x200) != 0 : null;

	// A plugin's flags as last read, with the file's time and size then: read again only when the file changed. A run asks
	// for the master flag of every plugin in the load order after each step (for each placement rule, in both order files) - on
	// a list of 1,200 plugins that was thousands of files opened every time
	static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, long Size, uint Flags)> s_flags = new(StringComparer.OrdinalIgnoreCase);

	// the TES4 record's flags; 0 when the file is not a plugin; null when it cannot be read
	static uint? HeaderFlags(string a_path)
	{
		try {
			var info = new FileInfo(a_path);
			if (!info.Exists) {
				return null;
			}
			if (s_flags.TryGetValue(a_path, out var known) && known.Stamp == info.LastWriteTimeUtc && known.Size == info.Length) {
				return known.Flags;
			}
			using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			var h = new byte[12];
			var flags = fs.ReadAtLeast(h, 12, false) == 12 && Encoding.ASCII.GetString(h, 0, 4) == "TES4" ? BitConverter.ToUInt32(h, 8) : 0;
			// (never without end: a load order has a few thousand plugins at most)
			if (s_flags.Count > 20000) {
				s_flags.Clear();
			}
			s_flags[a_path] = (info.LastWriteTimeUtc, info.Length, flags);
			return flags;
		} catch (IOException) {
			return null;
		} catch (UnauthorizedAccessException) {
			return null;
		}
	}

	// ESM-flagged (TES4 record flag bit 0) or an .esm file
	public static bool IsMaster(string a_path)
	{
		// the game loads every .esl in the master block too, flagged or not
		return a_path.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || a_path.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) ||
		       ((HeaderFlags(a_path) ?? 0) & 1) != 0;
	}
}
