using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace ToolRunner.App;

// The window in other languages. A language is { "the English words": "their translation" }: the program ships some inside itself
// (Languages\<its name>.json in the source), and a file of the same shape in the data folder's Languages folder adds a language or
// corrects a shipped one, text by text. Whatever a language leaves out stays English, and text a binding fills (what a run is
// doing) is left alone. Settings makes such a file from what the window shows.
public static class WindowWords
{
	public const string English = "English";

	const string ShippedPrefix = "Language.";

	// the languages inside the program
	public static List<string> Shipped() =>
		[.. typeof(WindowWords).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(ShippedPrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
			.Select(n => n[ShippedPrefix.Length..^".json".Length])];

	public static string? ShippedText(string a_name)
	{
		using var s = typeof(WindowWords).Assembly.GetManifestResourceStream(ShippedPrefix + a_name + ".json");
		if (s == null) {
			return null;
		}
		using var r = new StreamReader(s);
		return r.ReadToEnd();
	}

	static Dictionary<string, string> s_words = new(StringComparer.Ordinal);
	// back from a translation to its English, so a file made with a language in use still has English keys
	static Dictionary<string, string> s_english = new(StringComparer.Ordinal);

	public static string Folder => Path.Combine(AppPaths.Data, "Languages");
	static string PickedFile => Path.Combine(AppPaths.Data, "language.txt");

	public static string Picked()
	{
		try {
			return File.Exists(PickedFile) ? File.ReadAllText(PickedFile).Trim() : English;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return English;
		}
	}

	public static List<string> Available()
	{
		var names = new List<string>(Shipped());
		try {
			if (Directory.Exists(Folder)) {
				names.AddRange(Directory.GetFiles(Folder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!));
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
		}
		return [English, .. names.Where(n => n != English).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
	}

	public static void Pick(string a_name)
	{
		Directory.CreateDirectory(AppPaths.Data);
		Files.WriteAtomic(PickedFile, a_name);
	}

	// a text the code shows in a list (not one the window's walk reaches) in the language in use
	public static string Word(string a_english) => s_words.TryGetValue(a_english, out var w) ? w : a_english;

	// a text with names put into it ("Run only {0}"): the language's own form of it, the names as they are; a translation whose
	// places do not fit falls back to the English form
	public static string Format(string a_english, params object[] a_args)
	{
		try {
			return string.Format(System.Globalization.CultureInfo.CurrentCulture, Word(a_english), a_args);
		} catch (FormatException) {
			return string.Format(System.Globalization.CultureInfo.CurrentCulture, a_english, a_args);
		}
	}

	public static void Use(IReadOnlyDictionary<string, string> a_words)
	{
		s_words = a_words.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
		s_english = new(StringComparer.Ordinal);
		foreach (var (english, word) in s_words) {
			s_english.TryAdd(word, english);
		}
	}

	// the words of a language file; null, with why, when it does not read
	public static Dictionary<string, string>? Read(string a_text, out string a_problem)
	{
		a_problem = "";
		try {
			return JsonSerializer.Deserialize<Dictionary<string, string>>(a_text, new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip }) ?? [];
		} catch (JsonException e) {
			a_problem = e.Message;
			return null;
		}
	}

	// at the start, before the window is drawn; returns what went wrong with the picked file ("" = nothing): the window stays in English then
	public static string LoadPicked()
	{
		var name = Picked();
		if (name == English) {
			return "";
		}
		// a name only: this file is read at every start, and what it names must stay in the languages folder
		if (!GoodName(name)) {
			return $"the language {name} is no language's name: the window stays in English";
		}
		var file = Path.Combine(Folder, name + ".json");
		// the shipped words first, then the player's own file over them, text by text
		var words = new Dictionary<string, string>(StringComparer.Ordinal);
		var shipped = Shipped().FirstOrDefault(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } sn ? ShippedText(sn) : null;
		if (shipped != null && Read(shipped, out _) is { } inside) {
			foreach (var (k, v) in inside) {
				words[k] = v;
			}
		}
		try {
			if (File.Exists(file)) {
				if (Read(Files.ReadSharedText(file), out var problem) is not { } own) {
					if (shipped == null) {
						return $"the language file {name} does not read ({problem}): the window stays in English";
					}
					Use(words);
					return $"your language file {name} does not read ({problem}): the window uses the shipped {name} without it";
				}
				foreach (var (k, v) in own.Where(p => !string.IsNullOrWhiteSpace(p.Value))) {
					words[k] = v;
				}
			} else if (shipped == null) {
				return $"the language {name} was not found: the window stays in English";
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return $"the language file {name} could not be read ({e.Message}): the window stays in English";
		}
		Use(words);
		return "";
	}

	public static bool GoodName(string a_name) => a_name.Trim().Length > 0 && a_name == Path.GetFileName(a_name) && a_name is not ("." or "..");

	public static void OnEveryWindow() =>
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((a_s, _) => Tree((Window)a_s)));

	// every piece of text under a_root that no binding fills: each one seen is given to a_seen, and put in the language when one is in use
	static void Walk(DependencyObject a_root, Action<string>? a_seen)
	{
		// written only where a translation differs: a value set here would end a theme's resource, a style's value or a text's inlines
		void Do(string a_text, Action<string> a_set)
		{
			a_seen?.Invoke(s_english.GetValueOrDefault(a_text, a_text));
			if (s_words.TryGetValue(a_text, out var word) && word != a_text) {
				a_set(word);
			}
		}
		bool Free(DependencyProperty a_p) => !BindingOperations.IsDataBound(a_root, a_p);
		switch (a_root) {
			case TextBlock t when Free(TextBlock.TextProperty) && !string.IsNullOrWhiteSpace(t.Text):
				Do(t.Text, w => t.Text = w);
				break;
			case ContentControl c when Free(ContentControl.ContentProperty) && c.Content is string s && !string.IsNullOrWhiteSpace(s):
				Do(s, w => c.Content = w);
				break;
			case HeaderedItemsControl h when Free(HeaderedItemsControl.HeaderProperty) && h.Header is string hs && !string.IsNullOrWhiteSpace(hs):
				Do(hs, w => h.Header = w);
				break;
		}
		// a window's own title (its title bar and the taskbar's list of windows)
		if (a_root is Window win && Free(Window.TitleProperty) && !string.IsNullOrWhiteSpace(win.Title)) {
			Do(win.Title, w => win.Title = w);
		}
		if (a_root is FrameworkElement fe) {
			if (Free(FrameworkElement.ToolTipProperty) && fe.ToolTip is string tip && !string.IsNullOrWhiteSpace(tip)) {
				Do(tip, w => fe.ToolTip = w);
			}
			if (Free(AutomationProperties.NameProperty) && AutomationProperties.GetName(fe) is { Length: > 0 } name) {
				Do(name, w => AutomationProperties.SetName(fe, w));
			}
			// a context menu is no logical child of what it opens on
			if (fe.ContextMenu is { } menu) {
				Walk(menu, a_seen);
			}
		}
		foreach (var child in LogicalTreeHelper.GetChildren(a_root).OfType<DependencyObject>().ToList()) {
			Walk(child, a_seen);
		}
	}

	public static void Tree(DependencyObject a_root)
	{
		if (s_words.Count > 0) {
			Walk(a_root, null);
		}
	}

	public static List<string> Seen(DependencyObject a_root)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		Walk(a_root, s => seen.Add(s));
		return [.. seen.Order(StringComparer.Ordinal)];
	}

	// a language file made from what the window shows: new words added with no translation, what a file already holds kept
	public static string WriteTemplate(DependencyObject a_root, string a_name = "My language")
	{
		Directory.CreateDirectory(Folder);
		var file = Path.Combine(Folder, a_name + ".json");
		var words = new SortedDictionary<string, string>(StringComparer.Ordinal);
		if (File.Exists(file)) {
			// a translator's work is never replaced by an empty file
			var had = Read(Files.ReadSharedText(file), out var problem) ?? throw new InvalidDataException($"{file} does not read ({problem}): fix it or move it first, so your work is kept");
			foreach (var (k, v) in had) {
				words[k] = v ?? "";
			}
		}
		foreach (var s in Seen(a_root)) {
			words.TryAdd(s, "");
		}
		// two English texts with one translation read back as one: the language in use still has both
		foreach (var k in s_words.Keys) {
			words.TryAdd(k, "");
		}
		Files.WriteAtomic(file, JsonSerializer.Serialize(words, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
		return file;
	}
}
