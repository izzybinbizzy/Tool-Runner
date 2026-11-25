using System.Text.Json;

namespace ToolRunner;

// How every JSON file is read into nodes. A name written twice in one object reads without a word and then fails at the
// first look into that object, with an error no reader of a file expects (a setup with "minFreeGb" twice ended the window
// when it was picked; an add-on file or a theme with one did the same to whoever listed it). Refused where the file is
// read, it is a file that does not read - with the name said - and every reader already knows what to do with one
public static class JsonRead
{
	// a file a program wrote
	public static readonly JsonDocumentOptions Plain = new() { AllowDuplicateProperties = false };

	// a file a person may have written: comments and a last comma are allowed
	public static readonly JsonDocumentOptions ByHand = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, AllowDuplicateProperties = false };
}
