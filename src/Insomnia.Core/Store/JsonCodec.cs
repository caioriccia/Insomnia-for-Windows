using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Insomnia.Core.Store;

// Dentro do namespace: o namespace Insomnia.Core.Session (frente 'sessao') venceria o record.
using Insomnia.Core.Model;

/// Formatos JSON congelados do desenho §4 (session.json, state.json, config.json).
///
/// System.Text.Json, camelCase, indentado, nulos omitidos na escrita, membros desconhecidos
/// ignorados na leitura. A leitura passa por DTOs próprios (e não pelos records do modelo)
/// para ter controle total do que é obrigatório:
///   - session.json: startedAt e endsAt obrigatórios; qualquer falha = ilegível;
///   - state.json: campo ausente = padrão (null, [], false); campo presente com forma errada = corrompido;
///   - config.json: campo ausente = valor de Config.Default (como o original); forma errada = corrompido.
/// "version" ausente vale 1; qualquer versão diferente de 1 é recusada (formato que este build não conhece).
///
/// Parse* lança FormatException com o motivo; quem chama (FileStore) traduz para StoreCorruptException.
public static partial class JsonCodec
{
    public const int CurrentVersion = 1;

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            NumberHandling = JsonNumberHandling.Strict,
        };
        o.Converters.Add(new StrictDateTimeOffsetConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    // ---------- session.json ----------

    public static string SerializeSession(Session session) => JsonSerializer.Serialize(new SessionDto
    {
        Version = session.Version,
        StartedAt = session.StartedAt,
        EndsAt = session.EndsAt,
        Extensions = session.Extensions.IsDefault ? [] : [.. session.Extensions],
    }, Options);

    public static Session ParseSession(string json)
    {
        var dto = Deserialize<SessionDto>(json);
        var version = CheckVersion(dto.Version);
        var extensions = dto.Extensions ?? [];
        return new Session(dto.StartedAt, dto.EndsAt, [.. extensions]) { Version = version };
    }

    // ---------- state.json ----------

    public static string SerializeJournal(Journal journal) => JsonSerializer.Serialize(new JournalDto
    {
        Version = journal.Version,
        LidAction = journal.LidAction is { } l ? new LidActionDto
        {
            SchemeId = l.SchemeId, AcPrevious = l.AcPrevious, DcPrevious = l.DcPrevious, SetAt = l.SetAt,
        } : null,
        EnergySaver = journal.EnergySaver is { } e ? new EnergySaverDto
        {
            SchemeId = e.SchemeId, DcThresholdPrevious = e.DcThresholdPrevious, SetAt = e.SetAt, Cause = e.Cause,
        } : null,
        FrozenProcesses = journal.FrozenProcesses.IsDefault ? [] : [.. journal.FrozenProcesses.Select(p => new FrozenProcessDto
        {
            Pid = p.Pid, Name = p.Name, CreationTime = p.CreationTime, BootId = p.BootId, Group = p.Group,
        })],
        DockerFrozen = journal.DockerFrozen,
        SavedAudioOutputs = journal.SavedAudioOutputs.IsDefault ? [] : [.. journal.SavedAudioOutputs.Select(a => new SavedAudioOutputDto
        {
            DeviceId = a.DeviceId, Name = a.Name, Volume = a.Volume, Muted = a.Muted,
        })],
    }, Options);

    public static Journal ParseJournal(string json)
    {
        var dto = Deserialize<JournalDto>(json);
        var version = CheckVersion(dto.Version);
        if (dto.EnergySaver is { } es && es.Cause is not ("battery" or "lid"))
            throw new FormatException($"energySaver.cause inválido: \"{es.Cause}\"");
        return new Journal(
            version,
            dto.LidAction is { } l ? new LidActionEntry(l.SchemeId, l.AcPrevious, l.DcPrevious, l.SetAt) : null,
            dto.EnergySaver is { } e ? new EnergySaverEntry(e.SchemeId, e.DcThresholdPrevious, e.SetAt, e.Cause) : null,
            [.. NoNulls(dto.FrozenProcesses, "frozenProcesses").Select(p => new FrozenProcess(p.Pid, p.Name, p.CreationTime, p.BootId, p.Group))],
            dto.DockerFrozen ?? false,
            [.. NoNulls(dto.SavedAudioOutputs, "savedAudioOutputs").Select(a => new SavedAudioOutput(a.DeviceId, a.Name, a.Volume, a.Muted))]);
    }

    // ---------- config.json ----------

    public static string SerializeConfig(Config c) => JsonSerializer.Serialize(new ConfigDto
    {
        Version = c.Version,
        Presets = Array(c.Presets),
        DefaultPreset = c.DefaultPreset,
        MaxDuration = c.MaxDuration,
        FreezeList = Array(c.FreezeList),
        FreezeAllApps = c.FreezeAllApps,
        DockerRule = c.DockerRule,
        MuteOnLidClose = c.MuteOnLidClose,
        DarkenDisplayOnLidClose = c.DarkenDisplayOnLidClose,
        EnergySaverOnLidClose = c.EnergySaverOnLidClose,
        AgentList = Array(c.AgentList),
        LowPowerFloor = c.LowPowerFloor,
        EndFloor = c.EndFloor,
        HotspotSsid = c.HotspotSsid,
        LaunchAtLogin = c.LaunchAtLogin,
    }, Options);

    public static Config ParseConfig(string json)
    {
        var dto = Deserialize<ConfigDto>(json);
        var d = Config.Default;
        return new Config(
            CheckVersion(dto.Version),
            dto.Presets is null ? d.Presets : [.. dto.Presets],
            dto.DefaultPreset ?? d.DefaultPreset,
            dto.MaxDuration ?? d.MaxDuration,
            dto.FreezeList is null ? d.FreezeList : [.. NoNulls(dto.FreezeList, "freezeList")],
            dto.FreezeAllApps ?? d.FreezeAllApps,
            dto.DockerRule ?? d.DockerRule,
            dto.MuteOnLidClose ?? d.MuteOnLidClose,
            dto.DarkenDisplayOnLidClose ?? d.DarkenDisplayOnLidClose,
            dto.EnergySaverOnLidClose ?? d.EnergySaverOnLidClose,
            dto.AgentList is null ? d.AgentList : [.. NoNulls(dto.AgentList, "agentList")],
            dto.LowPowerFloor ?? d.LowPowerFloor,
            dto.EndFloor ?? d.EndFloor,
            dto.HotspotSsid ?? d.HotspotSsid,
            dto.LaunchAtLogin ?? d.LaunchAtLogin);
    }

    // ---------- comuns ----------

    private static T Deserialize<T>(string json) where T : class
    {
        T? value;
        try { value = JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException ex) { throw new FormatException(ex.Message, ex); }
        return value ?? throw new FormatException("conteúdo é null, esperado um objeto");
    }

    private static int CheckVersion(int? version)
    {
        var v = version ?? CurrentVersion;
        return v == CurrentVersion ? v : throw new FormatException($"versão {v} desconhecida (este build lê a {CurrentVersion})");
    }

    private static T[] NoNulls<T>(T?[]? items, string field) where T : class =>
        items is null ? [] : [.. items.Select(i => i ?? throw new FormatException($"{field} contém null"))];

    private static T[] Array<T>(ImmutableArray<T> items) => items.IsDefault ? [] : [.. items];

    // ---------- datas ----------

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    /// Lê uma data no formato aceito pelos arquivos; null se a forma não for aceita.
    /// Aceita só "yyyy-MM-ddTHH:mm:ss[.f]Z" ou "yyyy-MM-ddTHH:mm:ss[.f]±HH:MM".
    public static DateTimeOffset? TryParseDate(string text)
    {
        if (!DatePattern().IsMatch(text)) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
    }

    /// Escreve sempre em UTC, sem fração: "yyyy-MM-ddTHH:mm:ssZ".
    public static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);

    /// Conversor estrito: qualquer outro formato de data torna o arquivo ilegível.
    public sealed class StrictDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("data precisa ser texto");
            var text = reader.GetString()!;
            return TryParseDate(text) ?? throw new JsonException($"data fora do formato aceito: \"{text}\"");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(FormatDate(value));
    }

    // ---------- DTOs (forma exata em disco) ----------

    private sealed class SessionDto
    {
        public int? Version { get; set; }
        public required DateTimeOffset StartedAt { get; set; }
        public required DateTimeOffset EndsAt { get; set; }
        public int[]? Extensions { get; set; }
    }

    private sealed class JournalDto
    {
        public int? Version { get; set; }
        public LidActionDto? LidAction { get; set; }
        public EnergySaverDto? EnergySaver { get; set; }
        public FrozenProcessDto?[]? FrozenProcesses { get; set; }
        public bool? DockerFrozen { get; set; }
        public SavedAudioOutputDto?[]? SavedAudioOutputs { get; set; }
    }

    private sealed class LidActionDto
    {
        public required Guid SchemeId { get; set; }
        public required uint AcPrevious { get; set; }
        public required uint DcPrevious { get; set; }
        public required DateTimeOffset SetAt { get; set; }
    }

    private sealed class EnergySaverDto
    {
        public required Guid SchemeId { get; set; }
        public required uint DcThresholdPrevious { get; set; }
        public required DateTimeOffset SetAt { get; set; }
        public required string Cause { get; set; }
    }

    private sealed class FrozenProcessDto
    {
        public required int Pid { get; set; }
        public required string Name { get; set; }
        public DateTimeOffset? CreationTime { get; set; }
        public string? BootId { get; set; }
        public required string Group { get; set; }
    }

    private sealed class SavedAudioOutputDto
    {
        public required string DeviceId { get; set; }
        public string? Name { get; set; }
        public required float Volume { get; set; }
        public required bool Muted { get; set; }
    }

    private sealed class ConfigDto
    {
        public int? Version { get; set; }
        public int[]? Presets { get; set; }
        public int? DefaultPreset { get; set; }
        public int? MaxDuration { get; set; }
        public string?[]? FreezeList { get; set; }
        public bool? FreezeAllApps { get; set; }
        public bool? DockerRule { get; set; }
        public bool? MuteOnLidClose { get; set; }
        public bool? DarkenDisplayOnLidClose { get; set; }
        public bool? EnergySaverOnLidClose { get; set; }
        public string?[]? AgentList { get; set; }
        public int? LowPowerFloor { get; set; }
        public int? EndFloor { get; set; }
        public string? HotspotSsid { get; set; }
        public bool? LaunchAtLogin { get; set; }
    }
}
