namespace VectorAnimationEngine;

internal enum ShortcutCommandKind
{
    Tool,
    SelectionGroup,
    ShapeGroup,
    LineGroup,
    BrushGroup,
    PaintGroup,
    Vault
}

internal sealed record ShortcutCommandDefinition(
    string Id,
    string DisplayName,
    ShortcutCommandKind Kind,
    ToolMode? Tool = null);

internal sealed record ShortcutGestureRecord
{
    public string Key { get; init; } = "";
    public bool Control { get; init; }
    public bool Shift { get; init; }
    public bool Alt { get; init; }
}

internal sealed record ShortcutBindingRecord
{
    public string CommandId { get; init; } = "";
    public ShortcutGestureRecord[] Gestures { get; init; } = [];
}

internal sealed record ShortcutProfileRecord
{
    public int SchemaVersion { get; init; } = ShortcutProfiles.SchemaVersion;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? BasedOnProfileId { get; init; }
    public ShortcutBindingRecord[] Bindings { get; init; } = [];
}

internal readonly record struct ShortcutProfileNormalizationResult(
    string ActiveProfileId,
    ShortcutProfileRecord[] CustomProfiles,
    string[] Warnings);

internal static class ShortcutCommandIds
{
    public const string ToolSelect = "tool.select";
    public const string ToolTransform = "tool.transform";
    public const string ToolDistort = "tool.distort";
    public const string ToolHand = "tool.hand";
    public const string ToolRectangle = "tool.rectangle";
    public const string ToolEllipse = "tool.ellipse";
    public const string ToolTriangle = "tool.triangle";
    public const string ToolPolygon = "tool.polygon";
    public const string ToolStar = "tool.star";
    public const string ToolLine = "tool.line";
    public const string ToolPen = "tool.pen";
    public const string ToolSimplePen = "tool.simple-pen";
    public const string ToolPencil = "tool.pencil";
    public const string ToolBrush = "tool.brush";
    public const string ToolPressureBrush = "tool.pressure-brush";
    public const string ToolMixingBrush = "tool.mixing-brush";
    public const string ToolText = "tool.text";
    public const string ToolFill = "tool.fill";
    public const string ToolInkBottle = "tool.ink-bottle";
    public const string ToolEyedropper = "tool.eyedropper";
    public const string ToolGradient = "tool.gradient";
    public const string ToolEraser = "tool.eraser";
    public const string SelectionGroup = "tool-group.selection";
    public const string ShapeGroup = "tool-group.shape";
    public const string LineGroup = "tool-group.line";
    public const string BrushGroup = "tool-group.brush";
    public const string PaintGroup = "tool-group.paint";
    public const string ToggleVault = "view.vault.toggle";
}

internal static class ShortcutProfiles
{
    public const int SchemaVersion = 1;
    public const int MaximumCustomProfiles = 32;
    public const int MaximumBindingsPerProfile = 64;
    public const int MaximumGesturesPerCommand = 8;
    public const int MaximumProfileNameLength = 80;
    public const string TraditionalFlashProfileId = "builtin.traditional-flash.v1";
    public const string NumberKeysProfileId = "builtin.number-keys.v1";

    private static readonly Keys AllowedModifiers = Keys.Control | Keys.Shift | Keys.Alt;

    private static readonly ShortcutCommandDefinition[] CommandCatalog =
    [
        Tool(ShortcutCommandIds.ToolSelect, "Select", ToolMode.Select),
        Tool(ShortcutCommandIds.ToolTransform, "Free Transform", ToolMode.Transform),
        Tool(ShortcutCommandIds.ToolDistort, "Distort", ToolMode.Distort),
        Tool(ShortcutCommandIds.ToolHand, "Hand", ToolMode.Hand),
        Tool(ShortcutCommandIds.ToolRectangle, "Rectangle", ToolMode.Rectangle),
        Tool(ShortcutCommandIds.ToolEllipse, "Ellipse", ToolMode.Ellipse),
        Tool(ShortcutCommandIds.ToolTriangle, "Triangle", ToolMode.Triangle),
        Tool(ShortcutCommandIds.ToolPolygon, "Polygon", ToolMode.Polygon),
        Tool(ShortcutCommandIds.ToolStar, "Star", ToolMode.Star),
        Tool(ShortcutCommandIds.ToolLine, "Line", ToolMode.Line),
        Tool(ShortcutCommandIds.ToolPen, "Pen", ToolMode.Pen),
        Tool(ShortcutCommandIds.ToolSimplePen, "Simple Pen", ToolMode.SimplePen),
        Tool(ShortcutCommandIds.ToolPencil, "Pencil", ToolMode.Pencil),
        Tool(ShortcutCommandIds.ToolBrush, "Brush", ToolMode.Brush),
        Tool(ShortcutCommandIds.ToolPressureBrush, "Pressure Brush", ToolMode.PressureBrush),
        Tool(ShortcutCommandIds.ToolMixingBrush, "Mixing Brush", ToolMode.MixingBrush),
        Tool(ShortcutCommandIds.ToolText, "Text", ToolMode.Text),
        Tool(ShortcutCommandIds.ToolFill, "Fill", ToolMode.Fill),
        Tool(ShortcutCommandIds.ToolInkBottle, "Ink Bottle", ToolMode.InkBottle),
        Tool(ShortcutCommandIds.ToolEyedropper, "Eyedropper", ToolMode.Eyedropper),
        Tool(ShortcutCommandIds.ToolGradient, "Gradient", ToolMode.Gradient),
        Tool(ShortcutCommandIds.ToolEraser, "Eraser", ToolMode.Eraser),
        new(ShortcutCommandIds.SelectionGroup, "Selection group", ShortcutCommandKind.SelectionGroup),
        new(ShortcutCommandIds.ShapeGroup, "Shape group", ShortcutCommandKind.ShapeGroup),
        new(ShortcutCommandIds.LineGroup, "Line group", ShortcutCommandKind.LineGroup),
        new(ShortcutCommandIds.BrushGroup, "Brush group", ShortcutCommandKind.BrushGroup),
        new(ShortcutCommandIds.PaintGroup, "Fill group", ShortcutCommandKind.PaintGroup),
        new(ShortcutCommandIds.ToggleVault, "Vault", ShortcutCommandKind.Vault)
    ];

    private static readonly IReadOnlyDictionary<string, ShortcutCommandDefinition> CommandsById =
        CommandCatalog.ToDictionary(command => command.Id, StringComparer.Ordinal);

    private static readonly ShortcutProfileRecord TraditionalFlash = new()
    {
        Id = TraditionalFlashProfileId,
        Name = "Traditional Flash",
        Bindings =
        [
            Bind(ShortcutCommandIds.ToolSelect, Keys.V),
            Bind(ShortcutCommandIds.ToolTransform, Keys.Q),
            Bind(ShortcutCommandIds.ToolHand, Keys.H),
            Bind(ShortcutCommandIds.ToolRectangle, Keys.R),
            Bind(ShortcutCommandIds.ToolEllipse, Keys.O),
            Bind(ShortcutCommandIds.ToolLine, Keys.N),
            Bind(ShortcutCommandIds.ToolPen, Keys.P),
            Bind(ShortcutCommandIds.ToolPencil, Keys.Y),
            Bind(ShortcutCommandIds.ToolBrush, Keys.B),
            Bind(ShortcutCommandIds.ToolText, Keys.T),
            Bind(ShortcutCommandIds.ToolFill, Keys.K),
            Bind(ShortcutCommandIds.ToolInkBottle, Keys.S),
            Bind(ShortcutCommandIds.ToolEyedropper, Keys.I),
            Bind(ShortcutCommandIds.ToolGradient, Keys.G),
            Bind(ShortcutCommandIds.ToolEraser, Keys.E)
        ]
    };

    private static readonly ShortcutProfileRecord NumberKeys = new()
    {
        Id = NumberKeysProfileId,
        Name = "Number Keys",
        Bindings =
        [
            Bind(ShortcutCommandIds.SelectionGroup, Keys.D1, Keys.NumPad1),
            Bind(ShortcutCommandIds.ShapeGroup, Keys.D2, Keys.NumPad2),
            Bind(ShortcutCommandIds.LineGroup, Keys.D3, Keys.NumPad3),
            Bind(ShortcutCommandIds.BrushGroup, Keys.D4, Keys.NumPad4),
            Bind(ShortcutCommandIds.PaintGroup, Keys.D5, Keys.NumPad5),
            Bind(ShortcutCommandIds.ToolGradient, Keys.D6, Keys.NumPad6),
            Bind(ShortcutCommandIds.ToolEraser, Keys.D7, Keys.NumPad7),
            Bind(ShortcutCommandIds.ToolEyedropper, Keys.D8, Keys.NumPad8),
            Bind(ShortcutCommandIds.ToggleVault, Keys.D9, Keys.NumPad9)
        ]
    };

    public static IReadOnlyList<ShortcutCommandDefinition> Commands => CommandCatalog;

    public static IReadOnlyList<ShortcutProfileRecord> BuiltInProfiles =>
        [CloneProfile(TraditionalFlash), CloneProfile(NumberKeys)];

    public static string BuiltInProfileId(ToolShortcutPreset preset)
    {
        return preset == ToolShortcutPreset.NumberKeys ? NumberKeysProfileId : TraditionalFlashProfileId;
    }

    public static bool IsBuiltInProfileId(string? profileId)
    {
        return string.Equals(profileId, TraditionalFlashProfileId, StringComparison.Ordinal)
            || string.Equals(profileId, NumberKeysProfileId, StringComparison.Ordinal);
    }

    public static ToolShortcutPreset LegacyPresetForProfile(
        string? profileId,
        IEnumerable<ShortcutProfileRecord>? customProfiles = null)
    {
        var currentId = profileId?.Trim();
        var profiles = customProfiles?.ToArray() ?? [];
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(currentId) && visited.Add(currentId))
        {
            if (string.Equals(currentId, NumberKeysProfileId, StringComparison.Ordinal))
            {
                return ToolShortcutPreset.NumberKeys;
            }
            if (string.Equals(currentId, TraditionalFlashProfileId, StringComparison.Ordinal))
            {
                return ToolShortcutPreset.TraditionalFlash;
            }

            currentId = profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, currentId, StringComparison.Ordinal))?.BasedOnProfileId;
        }

        return ToolShortcutPreset.TraditionalFlash;
    }

    public static bool TryGetCommand(string? commandId, out ShortcutCommandDefinition command)
    {
        return CommandsById.TryGetValue(commandId ?? "", out command!);
    }

    public static ShortcutProfileRecord GetBuiltInProfile(ToolShortcutPreset preset)
    {
        return CloneProfile(preset == ToolShortcutPreset.NumberKeys ? NumberKeys : TraditionalFlash);
    }

    public static ShortcutProfileRecord? FindProfile(
        string? profileId,
        IEnumerable<ShortcutProfileRecord>? customProfiles = null)
    {
        if (string.Equals(profileId, TraditionalFlashProfileId, StringComparison.Ordinal))
        {
            return CloneProfile(TraditionalFlash);
        }

        if (string.Equals(profileId, NumberKeysProfileId, StringComparison.Ordinal))
        {
            return CloneProfile(NumberKeys);
        }

        var custom = customProfiles?.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.Ordinal));
        return custom is null ? null : CloneProfile(custom);
    }

    public static ShortcutProfileRecord ResolveProfile(
        string? profileId,
        IEnumerable<ShortcutProfileRecord>? customProfiles = null)
    {
        return FindProfile(profileId, customProfiles) ?? CloneProfile(TraditionalFlash);
    }

    public static IReadOnlyList<ShortcutProfileRecord> EnumerateProfiles(
        IEnumerable<ShortcutProfileRecord>? customProfiles)
    {
        var profiles = new List<ShortcutProfileRecord>
        {
            CloneProfile(TraditionalFlash),
            CloneProfile(NumberKeys)
        };
        if (customProfiles is not null) profiles.AddRange(customProfiles.Select(CloneProfile));
        return profiles;
    }

    public static ShortcutProfileNormalizationResult Normalize(
        string? activeProfileId,
        IEnumerable<ShortcutProfileRecord>? customProfiles,
        string? fallbackBuiltInProfileId = null)
    {
        var normalized = new List<ShortcutProfileRecord>();
        var warnings = new List<string>();
        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TraditionalFlashProfileId,
            NumberKeysProfileId
        };
        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TraditionalFlash.Name,
            NumberKeys.Name
        };

        foreach (var profile in customProfiles ?? [])
        {
            if (normalized.Count >= MaximumCustomProfiles)
            {
                warnings.Add($"Only the first {MaximumCustomProfiles} custom shortcut profiles were loaded.");
                break;
            }

            if (!TryNormalizeCustomProfile(profile, profileIds, profileNames, warnings, out var valid)) continue;
            normalized.Add(valid);
            profileIds.Add(valid.Id);
            profileNames.Add(valid.Name);
        }

        var fallback = IsBuiltInProfileId(fallbackBuiltInProfileId)
            ? fallbackBuiltInProfileId!
            : TraditionalFlashProfileId;
        var requestedId = activeProfileId?.Trim();
        var selectedId = profileIds.FirstOrDefault(profileId =>
            string.Equals(profileId, requestedId, StringComparison.OrdinalIgnoreCase)) ?? fallback;
        return new ShortcutProfileNormalizationResult(selectedId, normalized.ToArray(), warnings.ToArray());
    }

    public static ShortcutProfileRecord CreateCustomProfile(
        string? name,
        ShortcutProfileRecord source,
        IEnumerable<ShortcutProfileRecord>? existingCustomProfiles = null)
    {
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TraditionalFlash.Name,
            NumberKeys.Name
        };
        if (existingCustomProfiles is not null)
        {
            existingNames.UnionWith(existingCustomProfiles.Select(profile => profile.Name));
        }

        var baseName = TrimProfileName(name);
        if (baseName.Length == 0) baseName = TrimProfileName($"{source.Name} Copy");
        if (baseName.Length == 0) baseName = "Custom Profile";
        var uniqueName = UniqueProfileName(baseName, existingNames);
        return new ShortcutProfileRecord
        {
            Id = $"custom.{Guid.NewGuid():N}",
            Name = uniqueName,
            BasedOnProfileId = IsBuiltInProfileId(source.Id) ? source.Id : source.BasedOnProfileId,
            Bindings = CloneBindings(source.Bindings)
        };
    }

    public static bool TryRenameCustomProfile(
        ShortcutProfileRecord profile,
        string? name,
        IEnumerable<ShortcutProfileRecord>? existingCustomProfiles,
        out ShortcutProfileRecord renamed)
    {
        renamed = profile;
        if (!IsCustomProfileId(profile.Id)) return false;
        var normalizedName = TrimProfileName(name);
        if (normalizedName.Length == 0) return false;
        if (existingCustomProfiles?.Any(candidate =>
                !string.Equals(candidate.Id, profile.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Name, normalizedName, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return false;
        }

        if (string.Equals(normalizedName, TraditionalFlash.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedName, NumberKeys.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        renamed = profile with { Name = normalizedName };
        return true;
    }

    public static bool TrySetBinding(
        ShortcutProfileRecord profile,
        string commandId,
        IEnumerable<ShortcutGestureRecord>? gestures,
        bool replaceConflicts,
        out ShortcutProfileRecord updated,
        out string? conflictingCommandId,
        out string? error)
    {
        updated = profile;
        conflictingCommandId = null;
        error = null;
        if (!IsCustomProfileId(profile.Id))
        {
            error = "Built-in shortcut profiles are read-only.";
            return false;
        }

        if (!CommandsById.ContainsKey(commandId))
        {
            error = "The shortcut command is not supported.";
            return false;
        }

        var normalizedGestures = new List<ShortcutGestureRecord>();
        var gestureKeys = new HashSet<Keys>();
        foreach (var gesture in gestures ?? [])
        {
            if (normalizedGestures.Count >= MaximumGesturesPerCommand) break;
            if (!TryGetKeyData(gesture, out var keyData) || IsReservedGesture(keyData))
            {
                error = "The shortcut is invalid or reserved.";
                return false;
            }

            if (!gestureKeys.Add(keyData)) continue;
            TryCreateGesture(keyData, out var normalizedGesture);
            normalizedGestures.Add(normalizedGesture);
        }

        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in profile.Bindings)
        {
            if (string.Equals(binding.CommandId, commandId, StringComparison.Ordinal)) continue;
            if (binding.Gestures.Any(gesture =>
                    TryGetKeyData(gesture, out var keyData) && gestureKeys.Contains(keyData)))
            {
                conflicts.Add(binding.CommandId);
            }
        }

        if (conflicts.Count > 0 && !replaceConflicts)
        {
            conflictingCommandId = conflicts.First();
            return false;
        }

        var bindings = new List<ShortcutBindingRecord>();
        foreach (var binding in profile.Bindings)
        {
            if (string.Equals(binding.CommandId, commandId, StringComparison.Ordinal)) continue;
            if (!replaceConflicts || conflicts.Count == 0)
            {
                bindings.Add(CloneBinding(binding));
                continue;
            }

            var retained = binding.Gestures
                .Where(gesture => !TryGetKeyData(gesture, out var keyData) || !gestureKeys.Contains(keyData))
                .Select(CloneGesture)
                .ToArray();
            bindings.Add(binding with { Gestures = retained });
        }

        bindings.Add(new ShortcutBindingRecord
        {
            CommandId = commandId,
            Gestures = normalizedGestures.ToArray()
        });
        updated = profile with { Bindings = bindings.ToArray() };
        return true;
    }

    public static string? FindConflictCommandId(
        ShortcutProfileRecord profile,
        string commandId,
        ShortcutGestureRecord gesture)
    {
        if (!TryGetKeyData(gesture, out var expected)) return null;
        foreach (var binding in profile.Bindings)
        {
            if (string.Equals(binding.CommandId, commandId, StringComparison.Ordinal)) continue;
            if (binding.Gestures.Any(candidate =>
                    TryGetKeyData(candidate, out var keyData) && keyData == expected))
            {
                return binding.CommandId;
            }
        }

        return null;
    }

    public static bool TryCreateGesture(Keys keyData, out ShortcutGestureRecord gesture)
    {
        gesture = new ShortcutGestureRecord();
        var keyCode = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        if ((modifiers & ~AllowedModifiers) != Keys.None
            || keyData != (keyCode | modifiers)
            || !IsBindableKeyCode(keyCode))
        {
            return false;
        }

        var token = Enum.GetName(keyCode);
        if (string.IsNullOrEmpty(token)) return false;
        gesture = new ShortcutGestureRecord
        {
            Key = token,
            Control = (modifiers & Keys.Control) == Keys.Control,
            Shift = (modifiers & Keys.Shift) == Keys.Shift,
            Alt = (modifiers & Keys.Alt) == Keys.Alt
        };
        return true;
    }

    public static bool TryGetKeyData(ShortcutGestureRecord? gesture, out Keys keyData)
    {
        keyData = Keys.None;
        if (gesture is null
            || !Enum.TryParse<Keys>(gesture.Key?.Trim(), ignoreCase: true, out var keyCode)
            || !Enum.IsDefined(keyCode)
            || (keyCode & Keys.Modifiers) != Keys.None
            || !IsBindableKeyCode(keyCode))
        {
            return false;
        }

        keyData = keyCode;
        if (gesture.Control) keyData |= Keys.Control;
        if (gesture.Shift) keyData |= Keys.Shift;
        if (gesture.Alt) keyData |= Keys.Alt;
        return true;
    }

    public static string FormatGesture(ShortcutGestureRecord gesture)
    {
        return TryGetKeyData(gesture, out var keyData) ? FormatGesture(keyData) : "";
    }

    public static string FormatGesture(Keys keyData)
    {
        var parts = new List<string>(4);
        if ((keyData & Keys.Control) == Keys.Control) parts.Add("Ctrl");
        if ((keyData & Keys.Shift) == Keys.Shift) parts.Add("Shift");
        if ((keyData & Keys.Alt) == Keys.Alt) parts.Add("Alt");
        parts.Add(DisplayKey(keyData & Keys.KeyCode));
        return string.Join("+", parts);
    }

    public static string FormatGestures(IEnumerable<ShortcutGestureRecord>? gestures)
    {
        var keys = (gestures ?? [])
            .Select(gesture => TryGetKeyData(gesture, out var keyData) ? keyData : Keys.None)
            .Where(keyData => keyData != Keys.None)
            .Distinct()
            .ToList();
        CollapseNumberAliases(keys);
        return string.Join(" / ", keys.Select(FormatGesture));
    }

    public static int GetWorkspaceShortcutIndex(Keys keyData)
    {
        if ((keyData & Keys.Modifiers) != Keys.Control) return -1;
        var keyCode = keyData & Keys.KeyCode;
        return keyCode is >= Keys.D1 and <= Keys.D9
            ? (int)keyCode - (int)Keys.D1
            : -1;
    }

    public static bool IsReservedGesture(Keys keyData)
    {
        if (GetWorkspaceShortcutIndex(keyData) >= 0) return true;
        return keyData is Keys.F1
            or Keys.F2
            or (Keys.Alt | Keys.F4)
            or (Keys.Control | Keys.N)
            or (Keys.Control | Keys.O)
            or (Keys.Control | Keys.S)
            or (Keys.Control | Keys.Shift | Keys.S)
            or Keys.Space
            or Keys.Escape
            or Keys.Enter
            or Keys.Oemcomma
            or Keys.OemPeriod
            or (Keys.Shift | Keys.Oemcomma)
            or (Keys.Shift | Keys.OemPeriod)
            or Keys.F5
            or (Keys.Shift | Keys.F5)
            or Keys.F6
            or (Keys.Shift | Keys.F6)
            or Keys.F7
            or Keys.OemOpenBrackets
            or Keys.Oem6
            or Keys.Tab
            or (Keys.Shift | Keys.Tab)
            or (Keys.Control | Keys.Z)
            or (Keys.Control | Keys.X)
            or (Keys.Control | Keys.C)
            or (Keys.Control | Keys.V)
            or (Keys.Control | Keys.Up)
            or (Keys.Control | Keys.Down)
            or Keys.Delete;
    }

    public static bool HasContextualFixedShortcutConflict(Keys keyData)
    {
        return keyData is Keys.NumPad1
            or Keys.NumPad3
            or Keys.NumPad5
            or Keys.NumPad7
            or (Keys.Control | Keys.NumPad1)
            or (Keys.Control | Keys.NumPad3)
            or (Keys.Control | Keys.NumPad7)
            or Keys.Home
            or Keys.F
            or (Keys.Control | Keys.A)
            or (Keys.Shift | Keys.F2)
            or Keys.Left
            or Keys.Right
            or Keys.Up
            or Keys.Down
            or (Keys.Shift | Keys.Left)
            or (Keys.Shift | Keys.Right)
            or (Keys.Shift | Keys.Up)
            or (Keys.Shift | Keys.Down);
    }

    internal static ShortcutProfileRecord CloneProfile(ShortcutProfileRecord profile)
    {
        return profile with { Bindings = CloneBindings(profile.Bindings) };
    }

    private static bool TryNormalizeCustomProfile(
        ShortcutProfileRecord? profile,
        HashSet<string> profileIds,
        HashSet<string> profileNames,
        List<string> warnings,
        out ShortcutProfileRecord normalized)
    {
        normalized = new ShortcutProfileRecord();
        if (profile is null || profile.SchemaVersion != SchemaVersion || !IsCustomProfileId(profile.Id))
        {
            warnings.Add("A custom shortcut profile had an unsupported schema or ID and was ignored.");
            return false;
        }

        var id = profile.Id.Trim();
        var name = TrimProfileName(profile.Name);
        if (name.Length == 0 || profileIds.Contains(id) || profileNames.Contains(name))
        {
            warnings.Add($"Custom shortcut profile '{id}' had a duplicate ID or name and was ignored.");
            return false;
        }

        var bindings = new List<ShortcutBindingRecord>();
        var commands = new HashSet<string>(StringComparer.Ordinal);
        var assignedGestures = new HashSet<Keys>();
        foreach (var binding in (profile.Bindings ?? []).Take(MaximumBindingsPerProfile))
        {
            if (binding is null)
            {
                warnings.Add($"Profile '{name}' contained an empty command binding.");
                continue;
            }

            var commandId = binding.CommandId?.Trim() ?? "";
            if (!CommandsById.ContainsKey(commandId) || !commands.Add(commandId))
            {
                warnings.Add($"Profile '{name}' contained an unknown or duplicate command binding.");
                continue;
            }

            var gestures = new List<ShortcutGestureRecord>();
            var commandGestures = new HashSet<Keys>();
            foreach (var gesture in (binding.Gestures ?? []).Take(MaximumGesturesPerCommand))
            {
                if (!TryGetKeyData(gesture, out var keyData)
                    || IsReservedGesture(keyData)
                    || !commandGestures.Add(keyData))
                {
                    warnings.Add($"Profile '{name}' contained an invalid, reserved, or duplicate shortcut.");
                    continue;
                }

                if (!assignedGestures.Add(keyData))
                {
                    warnings.Add($"Profile '{name}' assigned one shortcut to multiple commands; the first binding was kept.");
                    continue;
                }

                TryCreateGesture(keyData, out var normalizedGesture);
                gestures.Add(normalizedGesture);
            }

            bindings.Add(new ShortcutBindingRecord
            {
                CommandId = commandId,
                Gestures = gestures.ToArray()
            });
        }

        normalized = new ShortcutProfileRecord
        {
            Id = id,
            Name = name,
            BasedOnProfileId = NormalizeOptionalId(profile.BasedOnProfileId),
            Bindings = bindings.ToArray()
        };
        return true;
    }

    public static bool IsCustomProfileId(string? profileId)
    {
        const string prefix = "custom.";
        var id = profileId?.Trim();
        return id is not null
            && id.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(id[prefix.Length..], "N", out _);
    }

    private static ShortcutCommandDefinition Tool(string id, string displayName, ToolMode tool)
    {
        return new ShortcutCommandDefinition(id, displayName, ShortcutCommandKind.Tool, tool);
    }

    private static ShortcutBindingRecord Bind(string commandId, params Keys[] gestures)
    {
        return new ShortcutBindingRecord
        {
            CommandId = commandId,
            Gestures = gestures.Select(keyData =>
            {
                if (!TryCreateGesture(keyData, out var gesture))
                {
                    throw new InvalidOperationException($"Invalid built-in shortcut: {keyData}.");
                }

                return gesture;
            }).ToArray()
        };
    }

    private static bool IsBindableKeyCode(Keys keyCode)
    {
        return keyCode != Keys.None
            && Enum.IsDefined(keyCode)
            && keyCode is not (Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
                or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
                or Keys.Menu or Keys.LMenu or Keys.RMenu
                or Keys.LWin or Keys.RWin);
    }

    private static string DisplayKey(Keys keyCode)
    {
        return keyCode switch
        {
            >= Keys.D0 and <= Keys.D9 => ((int)keyCode - (int)Keys.D0).ToString(),
            >= Keys.NumPad0 and <= Keys.NumPad9 => $"Num {(int)keyCode - (int)Keys.NumPad0}",
            Keys.OemOpenBrackets => "[",
            Keys.Oem6 => "]",
            Keys.Oemcomma => ",",
            Keys.OemPeriod => ".",
            _ => Enum.GetName(keyCode) ?? keyCode.ToString()
        };
    }

    private static void CollapseNumberAliases(List<Keys> keys)
    {
        for (var digit = 0; digit <= 9; digit++)
        {
            var topRow = (Keys)((int)Keys.D0 + digit);
            var numPad = (Keys)((int)Keys.NumPad0 + digit);
            if (!keys.Contains(topRow) || !keys.Contains(numPad)) continue;
            keys.Remove(numPad);
        }
    }

    private static string TrimProfileName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length <= MaximumProfileNameLength
            ? trimmed
            : trimmed[..MaximumProfileNameLength].TrimEnd();
    }

    private static string UniqueProfileName(string baseName, HashSet<string> existingNames)
    {
        if (!existingNames.Contains(baseName)) return baseName;
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var suffixText = $" {suffix}";
            var prefixLength = Math.Max(1, MaximumProfileNameLength - suffixText.Length);
            var candidate = $"{baseName[..Math.Min(baseName.Length, prefixLength)].TrimEnd()}{suffixText}";
            if (!existingNames.Contains(candidate)) return candidate;
        }

        return $"Custom {Guid.NewGuid():N}"[..MaximumProfileNameLength];
    }

    private static string? NormalizeOptionalId(string? id)
    {
        var trimmed = id?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= 96 ? trimmed : trimmed[..96];
    }

    private static ShortcutBindingRecord[] CloneBindings(IEnumerable<ShortcutBindingRecord>? bindings)
    {
        return (bindings ?? []).Select(CloneBinding).ToArray();
    }

    private static ShortcutBindingRecord CloneBinding(ShortcutBindingRecord binding)
    {
        return binding with { Gestures = (binding.Gestures ?? []).Select(CloneGesture).ToArray() };
    }

    private static ShortcutGestureRecord CloneGesture(ShortcutGestureRecord gesture)
    {
        return gesture with { };
    }
}
