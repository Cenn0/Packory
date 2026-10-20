using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// Deterministic, read-only checks of one immutable Skill snapshot. This profile
/// measures concrete format/documentation properties; it does not infer task
/// correctness, publisher identity, executable safety or an aggregate score.
/// </summary>
public static class SkillQualityAssessment
{
    public const string ProfileVersion = "packory.skill-quality/1";
    public const int MaxMarkdownFileBytes = 256 * 1024;
    public const int MaxMarkdownBytes = 1024 * 1024;
    public const int MaxLinks = 2048;
    public const int MaxFindings = 100;

    public sealed record Finding(string Code, string Dimension, string Severity, string Basis,
        string FileSha256, int? Line, string Message, string Suggestion);
    public sealed record Dimension(string Id, string Status, int ErrorCount, int WarningCount, int InfoCount);
    public sealed record Coverage(int ArchiveFiles, int MarkdownFiles, int InspectedMarkdownFiles,
        long InspectedMarkdownBytes, int LocalLinksChecked, int ExternalLinksNotChecked,
        int SkippedMarkdownFiles, IReadOnlyList<string> NotAssessed);
    public sealed record Report(string ProfileVersion, string Name, string Version,
        string ArchiveSha256, string ManifestSha256, bool ApprovalEnforced, string AssessmentStatus,
        Coverage Coverage, IReadOnlyList<Dimension> Dimensions, IReadOnlyList<Finding> Findings,
        bool FindingsTruncated);

    private static readonly Regex ReferenceDefinition = new(
        @"(?m)^ {0,3}\[(?<label>[^\]\r\n]{1,256})\]:[ \t]*(?<destination>[^\r\n]*)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(200));

    /// <summary>Callers authorize the request separately; unavailable, revoked
    /// and tampered revisions all yield null without exposing archive details.</summary>
    public static Report? AssessPublished(string name, string version, IPackageDatabase db,
        BlobStore blobs, SkillSecurityStore security, Settings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = PackoryMcpCatalog.Get(name, version, db, blobs, security, settings);
        if (snapshot is null) return null;
        var report = Assess(snapshot, settings.SkillSecurityEnforced, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Do not return cached assessment metadata after an intervening revocation.
        if (settings.SkillSecurityEnforced &&
            (security.Get(name, version) is not { State: "APPROVED" } approval ||
             !string.Equals(approval.Sha256, snapshot.ArchiveSha256, StringComparison.Ordinal)))
            return null;
        return report;
    }

    public static Report Assess(PackoryMcpCatalog.SkillSnapshot snapshot, bool approvalEnforced,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Files.Count is < 1 or > 512 ||
            snapshot.Files.Sum(x => (long)x.Bytes.Length) > 16L * 1024 * 1024 ||
            !ArtifactDigests.ValidSha256(snapshot.ArchiveSha256))
            throw new ArgumentException("A bounded, digest-bound Skill snapshot is required.");
        var manifest = snapshot.Files.SingleOrDefault(x => x.Path == "SKILL.md") ??
            throw new ArgumentException("A Skill manifest is required.");
        var state = new AssessmentState(snapshot, manifest, approvalEnforced, cancellationToken);
        return state.Run();
    }

    private sealed class AssessmentState(PackoryMcpCatalog.SkillSnapshot snapshot,
        PackoryMcpCatalog.SkillFile manifest, bool approvalEnforced, CancellationToken cancellationToken)
    {
        private readonly List<Finding> _findings = [];
        private readonly Dictionary<string, int[]> _counts = new(StringComparer.Ordinal)
        {
            ["frontmatter"] = new int[3], ["instructions"] = new int[3],
            ["local_references"] = new int[3], ["license_metadata"] = new int[3],
            ["coverage"] = new int[3]
        };
        private readonly string _manifestDigest = Digest(manifest.Bytes);
        private readonly HashSet<string> _files = snapshot.Files.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = snapshot.Directories.ToHashSet(StringComparer.Ordinal);
        private bool _incomplete;
        private int _findingCount;
        private int _inspected, _localLinks, _externalLinks, _skipped, _linksObserved;
        private long _inspectedBytes, _attemptedBytes;

        private void Add(string code, string dimension, string severity, string basis,
            string fileDigest, int? line, string message, string suggestion)
        {
            _counts[dimension][severity == "error" ? 0 : severity == "warning" ? 1 : 2]++;
            _findingCount++;
            if (_findings.Count < MaxFindings)
                _findings.Add(new Finding(code, dimension, severity, basis, fileDigest, line, message, suggestion));
        }

        public Report Run()
        {
            CheckFrontmatter();
            var markdown = snapshot.Files.Where(x => x.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Path == "SKILL.md" ? 0 : 1)
                .ThenBy(x => x.Path, StringComparer.Ordinal).ToArray();
            foreach (var file in markdown)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var digest = Digest(file.Bytes);
                if (file.Bytes.Length > MaxMarkdownFileBytes || _attemptedBytes + file.Bytes.Length > MaxMarkdownBytes)
                {
                    _skipped++;
                    _incomplete = true;
                    Add("QUALITY_MARKDOWN_LIMIT", "coverage", "warning", "coverage", digest, null,
                        "A Markdown document exceeds the per-file or total inspection budget.",
                        "Split large reference documents and rerun the static assessment; this document was not inspected.");
                    continue;
                }
                _attemptedBytes += file.Bytes.Length;
                string text;
                try { text = new UTF8Encoding(false, true).GetString(file.Bytes).Replace("\r\n", "\n").Replace('\r', '\n'); }
                catch (DecoderFallbackException)
                {
                    _skipped++;
                    _incomplete = true;
                    Add("QUALITY_INVALID_UTF8", "instructions", "error", "packory_check", digest, null,
                        "A Markdown document is not valid UTF-8.", "Save the document as valid UTF-8 before distribution.");
                    continue;
                }
                _inspected++;
                _inspectedBytes += file.Bytes.Length;
                if (file.Path == "SKILL.md") text = CheckManifestBody(text);
                try { CheckLinks(file.Path, digest, MaskExamples(text)); }
                catch (RegexMatchTimeoutException)
                {
                    _incomplete = true;
                    Add("QUALITY_PARSER_BUDGET", "coverage", "warning", "coverage", digest, null,
                        "Reference inspection exceeded its bounded parser budget.",
                        "Simplify the Markdown reference syntax and rerun; reference coverage is incomplete.");
                }
            }
            return new Report(ProfileVersion, snapshot.Name, snapshot.Version,
                snapshot.ArchiveSha256, _manifestDigest, approvalEnforced,
                _incomplete ? "incomplete" : "completed",
                new Coverage(snapshot.Files.Count, markdown.Length, _inspected, _inspectedBytes,
                    _localLinks, _externalLinks, _skipped,
                    ["behavioral_correctness", "script_execution", "non_markdown_file_contents", "malware_safety", "publisher_identity",
                     "license_rights", "external_link_availability", "markdown_anchor_targets",
                     "bare_file_references", "full_commonmark_conformance", "agent_connector_runtime"]),
                _counts.Select(x => new Dimension(x.Key,
                    x.Key == "coverage" && _incomplete ? "incomplete" :
                    x.Value[0] > 0 ? "issues_found" : x.Value[1] > 0 || x.Value[2] > 0 ? "suggestions" : "no_findings",
                    x.Value[0], x.Value[1], x.Value[2])).ToArray(), _findings,
                _findingCount > MaxFindings);
        }

        private void CheckFrontmatter()
        {
            var fields = snapshot.Frontmatter;
            static string? Text(JsonNode? n) => n is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            var name = Text(fields["name"]);
            if (name != snapshot.Name || name.Length is < 1 or > 64 ||
                name.StartsWith('-') || name.EndsWith('-') || name.Contains("--", StringComparison.Ordinal) ||
                name.Any(c => c != '-' && (c is < 'a' or > 'z') && (c is < '0' or > '9')))
                Add("QUALITY_NAME_FORMAT", "frontmatter", "error", "specification", _manifestDigest, null,
                    "The name does not follow Packory's ASCII Agent Skills naming profile.",
                    "Use 1–64 lowercase letters, digits and single hyphens, matching the skill directory.");
            var description = Text(fields["description"]);
            if (string.IsNullOrWhiteSpace(description) || description.Length > 1024)
                Add("QUALITY_DESCRIPTION_FORMAT", "frontmatter", "error", "specification", _manifestDigest, null,
                    "The description must be non-empty text of at most 1024 characters.",
                    "Provide a concise description of the task and when the skill applies.");
            if (fields.ContainsKey("compatibility") &&
                (Text(fields["compatibility"]) is not { Length: > 0 and <= 500 } compatibility || string.IsNullOrWhiteSpace(compatibility)))
                Add("QUALITY_COMPATIBILITY_FORMAT", "frontmatter", "error", "specification", _manifestDigest, null,
                    "The optional compatibility field must be non-empty text of at most 500 characters.",
                    "Describe concrete environment requirements or omit this optional field.");
            if (fields.ContainsKey("metadata") &&
                (fields["metadata"] is not JsonObject metadata || metadata.Any(x => Text(x.Value) is null)))
                Add("QUALITY_METADATA_FORMAT", "frontmatter", "error", "specification", _manifestDigest, null,
                    "The optional metadata field must map string keys to string values.",
                    "Quote scalar metadata values and replace nested objects with documented string values.");
            if (fields.ContainsKey("allowed-tools") && Text(fields["allowed-tools"]) is null)
                Add("QUALITY_ALLOWED_TOOLS_FORMAT", "frontmatter", "error", "specification", _manifestDigest, null,
                    "The optional allowed-tools declaration must be a string.",
                    "Use a space-separated declaration; a declaration does not grant permissions in Packory or a client.");
            if (!fields.ContainsKey("license"))
                Add("QUALITY_LICENSE_UNDECLARED", "license_metadata", "info", "recommendation", _manifestDigest, null,
                    "Optional license metadata is absent; reuse terms were not assessed.",
                    "If distribution terms are known, declare them or reference a bundled license file.");
            else if (Text(fields["license"]) is not { Length: > 0 } license || string.IsNullOrWhiteSpace(license))
                Add("QUALITY_LICENSE_FORMAT", "license_metadata", "error", "specification", _manifestDigest, null,
                    "The license declaration must be non-empty text when supplied.",
                    "Use a license name or a reference to the bundled license terms.");
            if (!fields.ContainsKey("compatibility") && snapshot.Files.Any(x => x.Path.StartsWith("scripts/", StringComparison.Ordinal)))
                Add("QUALITY_SCRIPT_REQUIREMENTS", "instructions", "info", "recommendation", _manifestDigest, null,
                    "Bundled scripts have no frontmatter compatibility declaration.",
                    "Consider documenting required runtimes, tools and network access if those requirements are not already in the instructions.");
        }

        private string CheckManifestBody(string text)
        {
            var end = text.StartsWith("---\n", StringComparison.Ordinal)
                ? text.IndexOf("\n---\n", 4, StringComparison.Ordinal) : -1;
            if (end < 0)
            {
                _incomplete = true;
                Add("QUALITY_FRONTMATTER_BOUNDARY", "frontmatter", "error", "specification", _manifestDigest, 1,
                    "The manifest does not contain a closed frontmatter block.",
                    "Place YAML frontmatter between delimiter lines before the Markdown instructions.");
                return "";
            }
            var bodyStart = end + 5;
            if (string.IsNullOrWhiteSpace(text[bodyStart..]))
                Add("QUALITY_EMPTY_INSTRUCTIONS", "instructions", "warning", "recommendation", _manifestDigest, null,
                    "The manifest has no instruction body.",
                    "Add actionable steps, input/output examples and relevant failure handling for the intended task.");
            if (text.Count(x => x == '\n') + (text.EndsWith('\n') ? 0 : 1) >= 500)
                Add("QUALITY_LONG_MANIFEST", "instructions", "info", "recommendation", _manifestDigest, null,
                    "The manifest reaches the 500-line guidance threshold.",
                    "Consider moving detailed material into linked reference files to reduce initial context use.");
            // Preserve positions so findings still address original document lines.
            return new string(text[..bodyStart].Select(x => x == '\n' ? '\n' : ' ').ToArray()) + text[bodyStart..];
        }

        private void CheckLinks(string sourcePath, string fileDigest, string text)
        {
            var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var masked = text.ToCharArray();
            foreach (Match definition in ReferenceDefinition.Matches(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryDestination(definition.Groups["destination"].Value, 0, false, out var target, out _))
                    definitions.TryAdd(NormalizeLabel(definition.Groups["label"].Value), target);
                Array.Fill(masked, ' ', definition.Index, definition.Length);
            }
            text = new string(masked);
            var line = 1;
            var countedThrough = 0;
            for (var index = 0; index < text.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (text[index] != '[' || IsEscaped(text, index)) continue;
                var end = FindLabelEnd(text, index);
                if (end < 0) continue;
                var next = end + 1;
                string? destination = null;
                var consumed = end;
                if (next < text.Length && text[next] == '(' &&
                    TryDestination(text, next + 1, true, out var inline, out var inlineEnd))
                {
                    destination = inline;
                    consumed = inlineEnd;
                }
                else if (next < text.Length && text[next] == '[')
                {
                    var referenceEnd = FindLabelEnd(text, next);
                    if (referenceEnd >= 0)
                    {
                        var label = text[(next + 1)..referenceEnd];
                        if (label.Length == 0) label = text[(index + 1)..end];
                        definitions.TryGetValue(NormalizeLabel(label), out destination);
                        consumed = referenceEnd;
                    }
                }
                else definitions.TryGetValue(NormalizeLabel(text[(index + 1)..end]), out destination);
                if (destination is not null)
                {
                    if (++_linksObserved > MaxLinks)
                    {
                        _incomplete = true;
                        Add("QUALITY_LINK_LIMIT", "coverage", "warning", "coverage", fileDigest, null,
                            "The archive exceeds the bounded link inspection budget.",
                            "Reduce duplicated links or split the skill; remaining links were not checked.");
                        return;
                    }
                    line += text.AsSpan(countedThrough, index - countedThrough).Count('\n');
                    countedThrough = index;
                    CheckTarget(sourcePath, fileDigest, line, destination);
                }
                index = consumed;
            }
        }

        private void CheckTarget(string sourcePath, string digest, int line, string target)
        {
            if (target.Length == 0 || target.StartsWith('#')) return;
            var windowsDrive = target.Length >= 2 && char.IsAsciiLetter(target[0]) && target[1] == ':';
            if (!windowsDrive && (target.StartsWith("//", StringComparison.Ordinal) ||
                Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme != "file")
               )
            { _externalLinks++; return; }
            _localLinks++;
            if (target.Length > 2048 || windowsDrive || target.StartsWith('/') || target.Contains('\\') ||
                target.Any(char.IsControl) || target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                BadTarget();
                return;
            }
            // Decode only after separating URI suffixes. Encoded separators, dot
            // segments or absolute paths are resolved as data within the archive.
            var suffix = target.IndexOfAny(['?', '#']);
            var encodedPath = suffix < 0 ? target : target[..suffix];
            string relative;
            try { relative = Uri.UnescapeDataString(encodedPath); }
            catch (UriFormatException) { BadTarget(); return; }
            if (relative.StartsWith('/') || relative.Contains('\\') || relative.Contains(':') || relative.Any(char.IsControl))
            { BadTarget(); return; }
            var parts = sourcePath.Split('/').SkipLast(1).ToList();
            foreach (var part in relative.Split('/'))
            {
                if (part is "" or ".") continue;
                if (part == "..")
                {
                    if (parts.Count == 0) { BadTarget(); return; }
                    parts.RemoveAt(parts.Count - 1);
                }
                else parts.Add(part);
            }
            var path = string.Join('/', parts);
            if (path.Length > 512) { BadTarget(); return; }
            if (!_files.Contains(path) && !_directories.Contains(path) && path.Length > 0)
                Add("QUALITY_MISSING_LOCAL_REFERENCE", "local_references", "error", "static_reference", digest, line,
                    "A relative Markdown link names a file or directory absent from this immutable archive.",
                    "Correct the link's case/path or include the referenced resource in a new skill revision.");
            return;

            void BadTarget() => Add("QUALITY_NONPORTABLE_LOCAL_REFERENCE", "local_references", "warning", "static_reference", digest, line,
                "A local Markdown link cannot be resolved within this archive.",
                "Replace machine-specific or escaping paths with a relative path to a bundled file.");
        }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool IsEscaped(string text, int index)
    {
        var slashes = 0;
        while (index > 0 && text[--index] == '\\') slashes++;
        return (slashes & 1) == 1;
    }
    private static string NormalizeLabel(string label) => string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static int FindLabelEnd(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < Math.Min(text.Length, start + 1024); i++)
        {
            if (IsEscaped(text, i)) continue;
            if (text[i] == '[') { if (++depth > 16) return -1; }
            if (text[i] == ']' && --depth == 0) return i;
        }
        return -1;
    }

    // A bounded inline/reference destination recognizer. The profile explicitly
    // excludes full CommonMark/HTML conformance and anchor-target validation.
    private static bool TryDestination(string text, int start, bool inline, out string target, out int end)
    {
        target = "";
        end = start;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        if (end >= text.Length) return !inline;
        var angle = text[end] == '<';
        if (angle) end++;
        var destination = new StringBuilder();
        var depth = 0;
        for (; end < text.Length && end - start <= 4096; end++)
        {
            var c = text[end];
            if (c is '\n' or '\r') return false;
            if (c == '\\' && end + 1 < text.Length && char.IsPunctuation(text[end + 1]))
            { destination.Append(text[++end]); continue; }
            if (angle && c == '>') { end++; break; }
            if (!angle)
            {
                if (c == '(' && ++depth > 16) return false;
                if (c == ')' && depth-- == 0) break;
                if (char.IsWhiteSpace(c)) break;
            }
            destination.Append(c);
        }
        if (destination.Length > 2048 || (angle && (end == 0 || text[end - 1] != '>'))) return false;
        target = destination.ToString();
        if (!inline) return true;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        // Optional title must be quoted and cannot span lines in this profile.
        if (end < text.Length && text[end] is '\'' or '"')
        {
            var quote = text[end++];
            while (end < text.Length && text[end] != quote && text[end] is not ('\r' or '\n'))
            { if (text[end] == '\\') end++; end++; }
            if (end >= text.Length || text[end++] != quote) return false;
            while (end < text.Length && text[end] is ' ' or '\t') end++;
        }
        return end < text.Length && text[end] == ')';
    }

    private static string MaskExamples(string text)
    {
        var result = text.ToCharArray();
        char fence = '\0';
        var fenceLength = 0;
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            var indent = line.TakeWhile(x => x == ' ').Count();
            var trimmed = line.AsSpan(Math.Min(indent, line.Length));
            var marker = trimmed.Length > 0 ? trimmed[0] : '\0';
            var run = 0;
            if (marker is '`' or '~') while (run < trimmed.Length && trimmed[run] == marker) run++;
            var wasFenced = fence != '\0';
            if (indent <= 3 && run >= 3)
            {
                if (fence == '\0') { fence = marker; fenceLength = run; }
                else if (marker == fence && run >= fenceLength && trimmed[run..].Trim().IsEmpty) fence = '\0';
            }
            if (wasFenced || fence != '\0' || indent >= 4 || line.StartsWith('\t'))
                Array.Fill(result, ' ', offset, line.Length);
            offset += line.Length + 1;
        }
        // Inline code and HTML comments are inert examples, not file links.
        var current = new string(result);
        for (var i = 0; i < current.Length; i++)
        {
            if (current.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = current.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0) end = current.Length; else end += 3;
                for (var n = i; n < end; n++) if (result[n] != '\n') result[n] = ' ';
                i = end - 1;
            }
            else if (current[i] == '`' && !IsEscaped(current, i))
            {
                var length = 1;
                while (i + length < current.Length && current[i + length] == '`') length++;
                var delimiter = new string('`', length);
                var end = current.IndexOf(delimiter, i + length, StringComparison.Ordinal);
                if (end >= 0)
                {
                    for (var n = i; n < end + length; n++) if (result[n] != '\n') result[n] = ' ';
                    i = end + length - 1;
                }
                else i += length - 1;
            }
        }
        return new string(result);
    }
}
