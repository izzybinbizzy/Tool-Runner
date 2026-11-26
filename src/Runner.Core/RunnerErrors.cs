namespace ToolRunner;

// One catalog of everything the runner itself refuses or fails on, each with what it means and what to do. A refusal's own text
// says what was found ("the game is running (SkyrimSE.exe)"); this says, the same way every time, what a player does about it -
// in the window under a start that was refused, in the simple log, on the command line and in its --json output. The tools' own
// errors are ToolErrors' (read off each tool's documentation); Explain.For asks both.
//
// An entry is found by words its text always holds, so the many places that word a refusal stay as they are. A test reads the
// engine's source and holds every refusal and every stopping finding of the check against this table: a new one without an
// entry fails the build's tests. The codes are for a bug report and the readme, and never change once given.
public static class RunnerErrors
{
	public sealed record Entry(string Code, string[] Any, string What, string Todo);

	static Entry E(string a_code, string a_any, string a_what, string a_todo) => new(a_code, a_any.Split('|'), a_what, a_todo);

	// the entry for what a tool itself said, when nothing closer is known
	public const string ToolsOwnWords = "TR-435";

	public static IReadOnlyList<Entry> All { get; } = [
		// ---- a start that was refused: nothing was touched
		E("TR-101", "another run is going|a run is going - learn|a run is going - test", "Only one run, put-back or look at a tool goes at a time.",
			"Wait for the run that is going, or stop it in the window (Stop now), then start again."),
		E("TR-102", "the setup does not read", "The setup file is damaged or is not a setup.",
			"Open Settings and save the setup again, or bring back a copy from the data folder's \"Setup backups\". A file edited by hand must stay valid JSON, with no name written twice in one place."),
		E("TR-103", "is damaged, so it cannot be told whether that run finished", "The record of the last run cannot be read, so the program cannot tell whether your profile was left half switched.",
			"Look at your profile in Mod Organizer: are the outputs you expect ticked? Then move that run's folder out of the Runs folder and start again."),
		E("TR-104", "cannot be read right now", "Another program holds the last run's record for a moment (an antivirus, a backup tool).",
			"Try again in a moment. If it keeps happening, leave the program's data folder out of what that other program scans."),
		E("TR-105", "did not finish - put the profile back first|this run did not finish - put your profile back first", "An earlier run was cut off (a crash, a power cut, the PC switched off) and outputs may still be off.",
			"Press \"Put my profile back\" at the top of the window (command line: restore). Nothing new starts until that is done."),
		E("TR-106", "is left out: this setup is set to a new game", "The save steps are left out while the setup is set to a new game.",
			"Untick \"New game\" beside START to run the clean save or the final save."),
		E("TR-107", "is not a step of this setup|is set to use its old output in this setup|this setup has no such step", "The step asked for is switched off in this setup, is not in it, or is set to use its old output.",
			"Tick the step in the list (and press \"Build it again\" for it there), or name one of the steps the message lists."),
		E("TR-108", "the first step comes after the last one", "The run was asked to go from a step to one that comes before it.",
			"Name the earlier step first, or move the steps in the list."),
		E("TR-109", "the check before the run could not finish", "Something the check must read could not be read (a drive that is not there, a folder with no access).",
			"Read the reason in the message, make that drive or folder reachable, and press Check (F5) to see that it now passes."),
		E("TR-110", "Mod Organizer was not found at|No ModOrganizer.ini beside Mod Organizer|No ModOrganizer.ini in the instance folder", "The setup points at a Mod Organizer that is not there (moved, renamed, or on a drive that is not plugged in).",
			"Open Settings and set Mod Organizer's program and its instance folder again, or make a new setup (New setup)."),
		E("TR-111", "Mod Organizer's program is not set|the MO2 profile is not set|the backup folder is not set|must be a whole path on this PC",
			"The setup is missing a folder or names one that is not a whole path on this PC.", "Open Settings, fill it in with a whole path (like D:\\Modding\\Backups), and save."),
		E("TR-112", "the setup has no steps|every step is switched off|only the save steps are switched on|uses its old output - nothing would run", "The setup has nothing to run.",
			"Tick at least one step in the list (for a new game the two save steps are left out, so tick a tool as well). When every ticked step uses its old output, " +
			"select one of them and press \"Build it again\" under the steps: a step set to use its old output does not run."),
		E("TR-113", "a step is listed twice|is no longer supported - Landscape Fixer replaced it", "The setup's step list holds a step twice, or one that Landscape Fixer replaced.",
			"Make a new setup for the profile (New setup), or take the named step out of the setup file. SnowFixer and AutoBlend were replaced by Landscape Fixer."),
		E("TR-114", "is not a mod's name", "A setting that must be a mod's name holds a path or a character Windows does not allow in a folder name.",
			"Open Settings and give the step a plain mod name there (one folder name, no \\ / : or \"..\"). A setup file from someone else is brought in with New setup > Import a setup file (or the import command), which checks this."),
		E("TR-115", "the run's log could not be opened", "The run's own folder could not be written (the disk is full, or the data folder lost its access).",
			"Free some room on the disk the program's data folder is on, or move the program to a folder you may write in, then start again."),
		E("TR-116", "command ended with exit code|command did not end within|could not start:", "A command this setup runs before or after a run failed.",
			"Open Settings > Run (the commands before and after a run), run that command yourself in a command window to see what it says, and fix or remove it."),
		E("TR-117", "stopped before anything was changed", "The run stopped before it switched anything: your mods are as they were.",
			"Read the reason after it. Nothing needs putting back - fix that and start again."),
		E("TR-118", "holds an entry that is not [plugin, place]|a place is last_master, after:<plugin> or end", "The setup's list of plugin places has an entry that is not a plugin with its place.",
			"Open Settings > Run > Plugin places after a run and give each entry a plugin and its place, like [\"MyPatch.esp\", \"after:Skyrim.esm\"] (the place is last_master, after:<plugin> or end), or take that entry out."),
		E("TR-119", "the setup could not be saved after PGLite switched", "PGLite is ticked, so the PGPatcher step was switched off, and the setup file would not take that change.",
			"Close any program that has the setup file open (an editor), make sure the data folder may be written, then start again."),
		E("TR-120", "the setup's name is not a plain name", "The name inside the setup file holds a folder part or a character Windows does not allow in a folder name; the run's own folder is named after it.",
			"Open Settings and give the setup a plain name, or bring a setup file from someone else in with New setup > Import a setup file (or the import command), which gives it one."),
		E("TR-121", "is already on record under this very time", "A run of this setup is on record with the very second this one began. The PC's clock was set back, or it is the hour a daylight-saving change repeats.",
			"Start again in a moment: the run's folder is named by its start time, and an earlier run's record is never written over."),

		// ---- the check before a run: the PC, Mod Organizer, the game
		E("TR-201", "Mod Organizer has no profile", "The profile this setup was made for is no longer in Mod Organizer (renamed or deleted).",
			"Rename the profile back, or make a new setup for the profile you use now (New setup)."),
		E("TR-202", "was started from inside Mod Organizer", "The program is running as one of Mod Organizer's own executables, where it cannot close and open Mod Organizer.",
			"Close it and start it from its own file or shortcut. (Mod Organizer's Tools menu entry is fine: that one starts it on its own.)"),
		E("TR-203", "the game is running|the game is still running|Skyrim is already running", "Skyrim (or its launcher) is open, and the tools and the in-game steps need the game closed.",
			"Close the game, then start again."),
		E("TR-204", "Mod Organizer is downloading", "Mod Organizer is in the middle of a download, and closing it would cut the download off.",
			"Let the download finish (or pause it in Mod Organizer's Downloads tab), then start again."),
		E("TR-205", "GB free on|not enough room on", "A disk the run writes to has less room than the run needs for its new outputs and for the old ones it keeps.",
			"Free room on that disk, or remove old run backups (Settings > Backups > Keep only the newest now), or point the backups or a tool's output folder at a roomier disk. " +
			"The floor is \"Free disk space needed (GB)\" in Settings > Run; with \"Do not start a run that will not fit on the disk\" switched off there the forecast is only told."),
		E("TR-206", "this step is not available in this version", "The setup names a step this version of the program does not have.",
			"Update the program, or untick that step."),
		E("TR-207", "its check could not read a file|its check found a setting of the wrong kind", "A file the step's check needs is locked or missing, or one of its settings holds the wrong kind of value.",
			"Read the file or setting named after it; close the program that holds the file, or correct the setting in Settings > Steps."),
		E("TR-208", "Steam is updating or checking Skyrim", "Steam is changing the game's files right now.",
			"Wait until Steam has finished, then start again. A new game version needs a matching SKSE and plugins before the in-game steps can work."),
		E("TR-209", "a Mod Organizer runs that the runner cannot read|a Mod Organizer runs that this program cannot read|started as administrator, or by another Windows user", "A Mod Organizer is open that Windows will not let this program look at or close (started as administrator, or by another Windows user).",
			"Close that Mod Organizer yourself, or start both programs the same way (both normally, or both as administrator), then start again."),
		E("TR-210", "is set to always run as administrator", "Mod Organizer is marked \"Run this program as an administrator\" and this program is not.",
			"Right-click ModOrganizer.exe > Properties > Compatibility and untick it, or start this program as administrator too."),
		E("TR-211", "another Mod Organizer is open", "A second Mod Organizer install is open, and a tool could be started in the wrong one.",
			"Close the other Mod Organizer, then start again."),
		E("TR-212", "full plugins are switched on - the game loads at most|light plugins (.esl or ESL-flagged) are switched on - the game loads at most", "More plugins are switched on than the game can load.",
			"Untick plugins you do not need, or make small ones light (ESL-flag them with xEdit where they allow it), until you are under the limit."),
		E("TR-213", "this Mod Organizer manages another game", "The Mod Organizer this setup points at is set up for another game (Skyrim VR, Enderal, the 2011 Skyrim, Fallout).",
			"Tool Runner works with Skyrim Special Edition and its Anniversary Edition (Steam and GOG). Point the setup at the Mod Organizer instance of that game: Settings > Setup."),
		E("TR-214", "Mod Organizer names no game folder|the game folder Mod Organizer names is not there", "Mod Organizer's settings name no game folder, or one that is gone (the game moved, a drive not plugged in).",
			"Open Mod Organizer and let it find the game again (it asks at its start when the folder is gone), close it, then press Check (F5)."),
		E("TR-215", "has not answered a start|is no longer where its record says", "Windows has not yet answered a start of Mod Organizer the run asked it for, or the Mod Organizer the run worked in has moved - so your profile is not written now.",
			"Wait the few minutes the message names, then press \"Put my profile back\" again. If Mod Organizer moved, put it back where it was (or start it once from there), then press it again."),

		// ---- the check before a run: a step's own settings and its tool
		E("TR-301", "no output mod is set|no output folder is set|the terrain LOD folder it reads is not set|no shader cache is set", "A step has no place to put what it makes.",
			"Open Settings > Steps, pick the step, and fill in its output mod (and its output folder where it has one). A new setup fills these in by itself."),
		E("TR-302", "is in (or holds)|is (or holds)|(the folder it writes into) holds a program",
			"A tool's output folder is one of Mod Organizer's own folders, the game's folder, the tool's own folder, a whole drive, or a folder you or Windows keep other things in.",
			"Give the step an empty folder of its own outside those (Settings > Steps > The tool's own output folder)."),
		E("TR-303", "no Mod Organizer executable is set for this step|Mod Organizer has no executable called|which does not exist", "The tool is not in Mod Organizer's Executables list under the name the setup uses, or its entry points at a file that is gone.",
			"Add or correct the tool in Mod Organizer (the gears button > Executables), or let the program add it: New setup > Find my tools. Then press Check (F5)."),
		E("TR-304", "program is not found", "A tool this step starts directly is not where the setup says.",
			"Open Settings > Steps and set the path of the tool's program; download the tool from its own page if you do not have it (Settings > Tools > TOOL UPDATES opens each page)."),
		E("TR-305", "this profile has no save to start the clean save from", "The clean save needs one of your saves, and this profile has none.",
			"Tick \"New game\" beside START if you will start a new character, or make a save in the game first."),
		E("TR-306", "the game route needs NGIO's PrecacheGrass.txt", "Making the grass cache with the game itself needs a file in the game folder, and this setup does not allow writing there.",
			"Use FasterNGIO for the grass step (Settings > Steps > Grass cache), or switch on Settings > Game steps > \"Allow writing into the game folder\"."),
		E("TR-307", "none of the shader mods is in this profile", "The shader cache step names shader mods this profile does not have.",
			"Untick the Shader caches step for this profile, or correct its shader mods in Settings > Steps."),
		E("TR-308", "\"on_crash\" is", "A step's setting holds a word it does not know.",
			"Open Settings > Steps and pick one of the words the message lists."),
		E("TR-309", "DynDOLOD is not in Expert mode", "DynDOLOD starts in its wizard, and the step needs its Expert window.",
			"Open DynDOLOD_SSE.ini in DynDOLOD's own folder (Edit Scripts\\DynDOLOD), set Expert=1, save, and press Check (F5)."),
		E("TR-313", "DynDOLOD looks unpacked over an older version", "A new DynDOLOD was unpacked over the old one, which its author says not to do: TexGen then refuses to start.",
			"Unpack the new version into a new, empty folder (dyndolod.info/Updating), copy your preset and settings files over, point Mod Organizer's Executables entries at it, and press Check (F5)."),
		E("TR-314", "DynDOLOD's preset does not use Ultra tree LOD", "TreeLODGen makes models only DynDOLOD's Ultra tree LOD uses, and the preset has it off.",
			"Tick Ultra in DynDOLOD's own window and save the preset again, or untick the TreeLODGen step."),
		E("TR-315", "DynDOLOD's preset file is not found", "The step is set to change DynDOLOD's saved preset, and that file is not where the setting says.",
			"Save a preset from DynDOLOD's own window and name it in Settings > Steps > DynDOLOD, or switch the step's terrain underside option off."),
		E("TR-316", "AnvilLOD needs DynDOLOD's folder", "AnvilLOD reads DynDOLOD's rule files and does not know where DynDOLOD is.",
			"Set DynDOLOD's folder in Settings > Steps > AnvilLOD."),
		E("TR-317", "would read as a command of its own", "FasterNGIO runs through a command line, and its program path or its arguments hold a character that command line would read as a command of its own.",
			"Take that character out of the grass step's arguments (Settings > Steps > Grass cache), or move FasterNGIO to a folder whose path has none of & | < > ^ \"."),
		E("TR-318", "a quote mark (\") stands in", "ACMOS is started with its road set and its LOD folder between quote marks, and one of them holds a quote mark of its own.",
			"Open Settings > Steps > ACMOS Road Generator and take the quote mark out of the road set's name or the terrain LOD folder."),
		E("TR-310", "the PGPatcher settings set is missing|but the PGPatcher settings have PGLite Mode", "PGPatcher's saved settings do not match this profile.",
			"Learn PGPatcher's settings again from its own window (Settings > Steps > PGPatcher > Learn its settings), or tick / untick PGLite in Mod Organizer to match."),
		E("TR-311", "its add-on file is gone|its add-on file:|changed since you allowed it|the mod that holds it|its program could not be opened|its add-on file changed while it was being read|its add-on file has problems", "An add-on step's file is missing, changed, or its mod is switched off.",
			"Tick the add-on's mod in Mod Organizer and look at the add-on again in Settings > Tools > ADD-ONS; allow it only if you trust what it starts."),
		E("TR-320", "it is set to use its old output, and there is none to use", "A step is set to use the output its tool made before, and that output mod is missing or empty.",
			"Select the step in the list and press \"Build it again\" under the steps, so the tool builds its output in this run. From the next run on that output can be used."),
		E("TR-321", "its old output cannot be used:|its old output cannot be used in this run:",
			"A plugin of the old output needs a plugin that is no longer in your load order, or one this run has switched off while its tools work: the game, or the tool that reads it, stops on a master that is missing.",
			"Select that step and press \"Build it again\" under the steps, so its output is built again for the load order you have now - or put the missing plugin's mod back in Mod Organizer. " +
			"When the message says \"in this run\", leaving the tools after that step out of the run works too."),
		E("TR-322", "no longer in the mod list",
			"An old output was to be switched on at its step's place, and its line was gone from the profile's mod list by then (taken out while the run went on).",
			"Press F5 in Mod Organizer so it lists that mod again (or put the mod back in its mods folder), then start the run again the way you started it - the step stays set to use its old output."),
		E("TR-323", "DynDOLOD and AnvilLOD are both switched on", "A profile uses one of the two LOD tools: each makes what the other makes, and keeps the other's output off.",
			"Untick the DynDOLOD step or the AnvilLOD step in the list, then start again."),
		E("TR-324", "have the same output mod", "Two steps are set to write into the same mod. A step moves what its output mod holds into the run's backup before it fills it, so the later one would put the earlier one's new output away.",
			"Open Settings > Steps and give each of the two an output mod of its own (a new setup names them \"<the tool's output> - <profile>\"), then start again."),
		E("TR-312", "is not in this profile's mod list", "A mod this step names is not in this profile's mod list.",
			"Install the mod or correct its name: in Mod Organizer, or in Settings > Steps where the step names it."),

		// ---- a run that stopped part way
		E("TR-401", "could NOT be put back|the profile is back except|put back except|could not put the profile back", "The run ended, but a file was held by another program, so part of your profile is not back yet.",
			"Close what holds the file (Mod Organizer, a tool left open, an Explorer preview), then press \"Put my profile back\". Until then that mod stays switched off, and its old output is safe in the run's backup."),
		E("TR-402", "the final save was not made|the save to play was not made", "Every tool finished and your outputs are on; only the last save in the game was not made.",
			"Load the clean save yourself, walk back to where you were, and save - or run only the Final save step (right-click it)."),
		E("TR-403", "Mod Organizer was not ready after|Mod Organizer did not close within|Mod Organizer's companion did not read the lists back", "Mod Organizer did not answer in time (a box of its own left open, a very large load order, a slow disk).",
			"Open Mod Organizer once by hand and answer whatever it asks, close it, then run again from this step."),
		E("TR-404", "the Mod Organizer you opened stayed open|Mod Organizer was in use for|Mod Organizer asked about downloads in progress|Mod Organizer asked to end programs that are still running",
			"You were using Mod Organizer, and the run never closes it under you.", "Finish what you were doing, close Mod Organizer, and press \"Run again from\" this step."),
		E("TR-405", "still runs from Mod Organizer's folders|is already running (process", "A program from an earlier start is still open (a tool, the game, a crash reporter), and Mod Organizer cannot start cleanly beside it.",
			"Close that program (the message names it), then run again from this step."),
		E("TR-407", "Steam is not running and its program could not be found|Steam did not start within", "A Steam copy of the game needs Steam running first.",
			"Start Steam, wait until it is signed in, then run again from this step."),
		E("TR-406", "did not start within|started and ended at once|ended as soon as it started|could not start |did not start FasterNGIO", "The tool or the game did not come up.",
			"Start it once yourself through Mod Organizer to see what it says (a missing requirement, a wrong path, an update box), fix that, then run again from this step. Settings > Tools > Test my tools checks every tool at once."),
		E("TR-408", "minute limit|sat idle for|closed before it finished|wrote nothing for|did not finish building within", "The tool ran longer than its limit, sat without working, or closed without finishing.",
			"Open the run's detailed log for the tool's last lines. A tool that only needs more time: raise its limit (Settings > Steps > Give up after; Synthesis building its patchers: Patcher build limit). One that asked a question nobody answered: answer it once in the tool's own window (Learn its settings), then run again from this step."),
		E("TR-409", "this step needs your hands in the game and nobody can answer here", "A guided step needs you, and the run was started where nobody can press Done (the command line, a queue left alone).",
			"Run that step from the window, or install DevBench so the in-game steps run by themselves."),
		E("TR-410", "the step's setting|this step has no Mod Organizer executable", "A setting the step needs is empty.",
			"Open Settings > Steps, fill it in, and save."),
		E("TR-411", "file(s) - too few|looks empty|came out with|came out empty", "The tool finished but made almost nothing, so its old output was put back.",
			"Open the tool once by hand and look at its own log: it usually found nothing to work on (no worldspace ticked, a missing master, a wrong output path). " +
			"A step that is right to make this little: lower \"Fewest files that counts as a result\" on its page in Settings > Steps."),
		E("TR-412", "opened on the player's screen", "A tool that runs hidden showed a window on your screen, so it was ended.",
			"Run again from this step. If it happens every time, switch \"Hidden\" off for that step in Settings > Steps, so the tool runs in the open."),
		E("TR-415", "the game shows a box this run does not know|the game's box did not close after its answer was pressed|the game shows a Windows box this step does not answer|the game shows a box this step does not answer|the game stopped at something this step does not know", "A mod, or the game, asked a question the run cannot answer for you, so the game was closed and the step stopped.",
			"Start the game yourself once and answer it (many such questions come only once), or switch off that mod's own setting that asks; then run again from this step. " +
			"While you are at the PC you can also answer it during the run: the window names the box and waits a minute before the step stops."),
		E("TR-416", "Faster Archives closed the game to ask|Its window is left open: choose|nobody chose for", "Faster Archives closes the game at its start when it finds archives it can repack, and asks in a window of its own. That is no crash.",
			"Choose in its window (Repack now, or Don't offer these again) and run again from this step - or set Settings > Game steps > Faster Archives' repack offer to skip it during the run, and it asks the next time you start the game yourself."),
		E("TR-413", "its learned settings name a file outside their folder|save, close it, and learn again|" + ToolSettings.SavedNothing, "The settings kept from the tool's own window are missing or damaged.",
			"Set the tool up again: Tools (at the top of the window) > Set up again - or Settings > Steps > Learn its settings -, change something in the tool's window, save, and close it."),

		// ---- a step that failed: the game, the tool's window, the tool's own words
		E("TR-417", "ended with exit code|ended with the code|wrote no new report", "A tool that works without a window ended with an error of its own.",
			"Everything it printed is kept with the run: History > Open its folder (the tool's \"output.txt\"), or the run's detailed log. Its last lines say what it could not do; put that right, then run again from this step."),
		E("TR-418", "did not answer within|never accepted the load|did not finish loading|were still compiling after|kept changing for",
			"The game took longer than the step allows to start, to load a save or to finish writing one.",
			"Start the game yourself through Mod Organizer and load that save: a box, a crash or a very long load shows there. A game that is only slow gets more time on the step's page in Settings > Steps (the game start limit and the waits). Then run again from this step."),
		E("TR-419", "the first start crashed|the game crashed|restart(s) this step allows|built no new grass file|stood down at start|gave no sign of running|nobody answered for|the step stops here",
			"The game crashed more often than the step allows, or the mod that does the step's work never started.",
			"Start the game yourself through Mod Organizer with the same mods ticked. A crash leaves a crash log beside your SKSE logs (crash-*.log) that names its cause; a grass cache needs NGIO and its grass mods set up as their own pages say, a shader cache needs the shader mod ticked and loading. Then run again from this step."),
		E("TR-420", "has no save of|no new save was made|\" was not made", "There is no save for the step to load, or the game did not write the save it was asked for.",
			"With no save yet: tick \"New game\" beside START (the two save steps are then left out), or make a save in the game first. A save that was not written is usually blocked by a mod: switch on Settings > Game steps > \"Force the save\", then run again from this step."),
		E("TR-421", "no DynDOLOD-off save|DynDOLOD did not switch off|DynDOLOD switched itself back on", "DynDOLOD could not be switched off in the game for the clean save, or this run has no save made with it off.",
			"Make the clean save by hand once, as DynDOLOD's own help describes (dyndolod.info/Help/Clean-Save), untick the clean save step, and run again from the step after it. A run that begins in the middle has no clean save of its own: start it from the clean save step."),
		E("TR-422", "no terrain LOD to paint", "ACMOS paints roads into the terrain LOD xLODGen makes, and found none.",
			"Tick the xLODGen step before it (or run xLODGen once), make sure both name the same terrain LOD folder in Settings > Steps, then run again from the xLODGen step."),
		E("TR-423", "preset still makes a terrain underside", "The step is set never to make a terrain underside, and the DynDOLOD preset it names could not be changed to say so.",
			"Open DynDOLOD by hand, load that preset, switch the terrain underside off there and save the preset again - or switch \"Never make a terrain underside\" off on the step's page in Settings > Steps."),
		E("TR-424", "PGPatcher's settings do not read|PGPatcher's settings have no output section|Settings.json in Mod Organizer's overwrite folder does not read|settings file is empty|settings file does not read as settings|settings file no longer holds",
			"The tool's own settings file is missing, is not valid, or lacks a part the step sets.",
			"Open the tool once through Mod Organizer, set it up and close it, so it writes its settings again; then run again from this step. A settings file the message names as broken can be moved away first."),
		E("TR-425", "which is switched off in this profile", "The tool's program is inside a mod that is switched off in this profile, and Mod Organizer cannot start a program from there.",
			"Tick that mod in Mod Organizer (the message names it), then run again from this step."),
		E("TR-426", "name one of that character's saves", "The save the step names belongs to another character than the one this setup loads.",
			"Name a save of that character on the step's page in Settings > Steps (or leave it empty for the newest one), or pick the other character in Settings > Setup > Character to load."),
		E("TR-427", "close what holds it|close what holds the profile's files|make the folder readable|could not all go into", "A file or folder the step must read or change is held by another program, or may not be read.",
			"Close what holds it (an editor, a sync or backup tool, a virus scan still going) or give the folder its access back, then run again from this step."),
		E("TR-428", "wrapper needs a folder path without spaces", "FasterNGIO is started through a small command file, and this PC's ProgramData folder has a blank in its path, which that route cannot take.",
			"Make the grass cache with the game itself instead: Settings > Steps > Grass cache > How: game, and Settings > Game steps > \"Allow writing into the game folder\"."),
		E("TR-429", "the game did not run", "DevBench, the mod the in-game steps work through, answered one of the step's calls with an error.",
			"Update DevBench and make sure it is ticked in this profile, then run again from this step. If it fails the same way, send a bug report with this run (History > Help me report a bug)."),
		E("TR-430", "runs no window of its own", "This step's tool has no window of its own to set up.",
			"Its options are the ones on the step's page in Settings > Steps."),
		E("TR-431", "would not list the running programs", "Windows did not hand over its list of running programs, so the step could not tell whether the game is open.",
			"Run again from this step in a moment. If it keeps happening, a security program is in the way: allow Tool Runner in it."),
		E("TR-432", "could not be held by this program", "Windows would not let this program hold a program the run started - as it does when that program runs as administrator and this one does not.",
			"Start both the same way (both normally, or both as administrator), then run again from this step."),
		E("TR-433", "no Generate button|worldspace list|worldspace ticks|no Start button|its last question has no|no usable|did not appear|closed before|closed when|window closed|did not start working when|did not start its patchers",
			"The tool's window did not show what this step looks for and presses.",
			"Settings > Tools > Test my tools opens each tool and says what it finds. The usual causes: a version of the tool newer or older than the step knows, a box the tool shows at its start, or (DynDOLOD) its Expert mode switched off. " +
			"Close such a box or set the tool up once by hand, then run again from this step; a tool that works by hand and still fails in a run is worth a bug report (History > Help me report a bug)."),
		E("TR-434", "did not write|wrote no new|without writing|wrote no log|wrote nothing|stopped before it wrote|did not save its plugins|instead of its output folder", "The tool closed or finished without the file it is there to make.",
			"Run the tool once by hand through Mod Organizer and watch it to its end: its window or its log says why it made nothing (a setting of its own, a requirement that is missing, an output folder it may not write into). Then run again from this step."),

		// ---- putting back and undoing
		E("TR-501", "that run is still going|a run or another put-back is going|a run or a put-back is going", "A run or a put-back is still at work.",
			"Wait until it has ended, then press the button again."),
		E("TR-502", "nothing was undone|could not be undone whole|the undo stopped half way", "The undo could not finish (a file held by another program, Mod Organizer moved).",
			"Close what holds the file and undo the run again from History. What already moved is on record, and nothing is lost: its files are in the run's backup."),
		E("TR-503", "this run was undone already|this run built nothing to undo|only the last run can be undone", "Only the last run that changed a profile can be undone, and only once.",
			"To go further back, bring an older output back by hand from that run's backup folder (History > Open its folder names it)."),
		E("TR-504", "this run's copy of your mod and plugin lists is gone|this run's backup is gone", "The files an undo needs were removed (old backups are tidied after each run, or the folder was cleared by hand).",
			"This run can no longer be undone. Raise \"Backups to keep\" in Settings > Backups to keep more of them in future."),

		// ---- what a tool itself said (last of all: any closer entry above goes first, and so does the tool's own documented
		// meaning - Explain.For)
		E(ToolsOwnWords, "reported an error|asks a question this step does not answer|refused to run|could not build its patchers|logged errors|showed a box this step does not know|still wants a build inside Mod Organizer|" +
			"stopped while it was loading|ACMOS did not finish|ACMOS: |PGPatcher: |Pandora: |Synthesis: |TexGen: |DynDOLOD: ", "The tool itself reported a problem, or asked something the step cannot answer for you.",
			"The message holds the tool's own words. Run the tool once by hand through Mod Organizer to see it in full, and look it up on the tool's own page (right-click the step). " +
			"A question it asks every time: answer it once in the tool's own window (Tools, at the top of the window), then run again from this step."),
	];

	// what a tool said follows these words: its own words may hold any entry's ("could not start", "did not write"), so only
	// the runner's words before them choose the entry
	const string ToolSaid = " reported an error: ";

	// the entry a refusal or failure text falls under (any letter case), or null when it is none of the runner's own
	public static Entry? Find(string a_text)
	{
		var cut = a_text.IndexOf(ToolSaid, StringComparison.OrdinalIgnoreCase);
		var ours = cut < 0 ? a_text : a_text[..(cut + ToolSaid.Length)];
		return All.FirstOrDefault(e => e.Any.Any(a => ours.Contains(a, StringComparison.OrdinalIgnoreCase)));
	}
}

// what a problem means and what to do about it, whoever's it is: the runner's own first, then the tools' documented errors
public static class Explain
{
	public sealed record Answer(string? Code, string What, string Todo, string? Help);

	public static Answer? For(string a_step, string a_text)
	{
		var own = RunnerErrors.Find(a_text);
		// what a tool itself said: its documented meaning (its author's own help) goes before the catalog's general words for it
		if ((own == null || own.Code == RunnerErrors.ToolsOwnWords) && ToolErrors.Explain(a_step, a_text) is { } tool) {
			return new Answer(null, tool.What, tool.Todo, tool.Help);
		}
		return own == null ? null : new Answer(own.Code, own.What, own.Todo, null);
	}

	// one refusal as lines for a person: what was found, then what to do (and nothing more when nobody knows better)
	public static List<string> Lines(string a_step, string a_text)
	{
		var l = new List<string> { a_text };
		if (For(a_step, a_text) is { } a) {
			l.Add("  What to do: " + a.Todo + (a.Code == null ? "" : $" [{a.Code}]"));
			if (a.Help != null) {
				l.Add("  Help: " + a.Help);
			}
		}
		return l;
	}
}
