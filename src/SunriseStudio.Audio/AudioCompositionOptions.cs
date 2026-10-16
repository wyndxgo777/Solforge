/** File: Choices (game syncs, states, variables, loop count) that steer how an audio event is composed. */
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SunriseStudio.Audio;

/** A switch or state value chosen for a group. */
public sealed record AudioGameSync(bool Switch, uint Group, uint Value);
/** A state value chosen for a state group. */
public sealed record AudioStateValue(uint Group, uint Value);
/** A game variable (RTPC) value. */
public sealed record AudioGameVariable(uint Id, float Value);

/** Preview choices are explicit inputs and never replace the native bank data. */
public sealed record AudioCompositionOptions
{
    public IReadOnlyList<AudioGameSync> GameSyncs { get; init; } = [];
    public IReadOnlyList<AudioStateValue> States { get; init; } = [];
    public IReadOnlyList<AudioGameVariable> Variables { get; init; } = [];
    public int LoopCount { get; init; } = 1;
    public bool IncludeInitialDelay { get; init; } = true;
    public bool IncludeEffectVolumes { get; init; } = true;
    public bool AllRandomChoices { get; init; }

    /** Throws unless the options are within limits, finite and unique per parameter. */
    public void Validate()
    {
        if (LoopCount is < 1 or > 16) throw new InvalidDataException("Choose 1 to 16 preview loops.");
        if (GameSyncs.Count > 128 || States.Count > 128 || Variables.Count > 128
            || GameSyncs.GroupBy(value => (value.Switch, value.Group)).Any(group => group.Count() != 1)
            || States.GroupBy(value => value.Group).Any(group => group.Count() != 1)
            || Variables.GroupBy(value => value.Id).Any(group => group.Count() != 1)
            || Variables.Any(value => !float.IsFinite(value.Value)))
            throw new InvalidDataException("Audio choices must be finite and unique for each parameter.");
    }

    /** Equal choices give equal keys; IDs are numeric and values use the invariant culture. */
    public string Key
    {
        get
        {
            Validate();
            var text = new StringBuilder();
            foreach (var value in GameSyncs.OrderBy(value => value.Switch).ThenBy(value => value.Group))
                text.Append(value.Switch ? 'w' : 's').Append(value.Group).Append('=').Append(value.Value).Append(';');
            text.Append('|');
            foreach (var value in States.OrderBy(value => value.Group)) text.Append(value.Group).Append('=').Append(value.Value).Append(';');
            text.Append('|');
            foreach (var value in Variables.OrderBy(value => value.Id))
                text.Append(value.Id).Append('=').Append(value.Value.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            text.Append('|').Append(LoopCount).Append('|').Append(IncludeInitialDelay ? 1 : 0)
                .Append(IncludeEffectVolumes ? 1 : 0).Append(AllRandomChoices ? 1 : 0);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
        }
    }
}
