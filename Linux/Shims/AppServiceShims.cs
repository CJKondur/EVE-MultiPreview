// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The Windows thumbnail, crop and hotkey services AppServices names, and the thumbnail window the settings read one number from.
// The Linux app hands AppServices null for all three services, so these are never reached. Even so, a call made while a
// page renders (GetThumbnailForCharacter, IsExcludedFromCycle's caller) answers "absent"; the calls an action makes throw.
using EveMultiPreview.Views;

namespace EveMultiPreview.Services
{
    public sealed class ThumbnailManager
    {
        /// <summary>The range the resize limit's width and height may take (the settings cards use it at build time).</summary>
        public const int MinFloor = 20, MaxFloor = 2000;

        /// <summary>Render time: no thumbnail is running here.</summary>
        public ThumbnailWindow? GetThumbnailForCharacter(string characterName) => null;

        /// <summary>Render time: the system a character is in is not known here.</summary>
        public string? GetCharacterSystem(string characterName) => null;

        public void ApplyOpacityToAll() => throw Unsupported(nameof(ApplyOpacityToAll));
        public void ApplySizeToAll(int w, int h) => throw Unsupported(nameof(ApplySizeToAll));
        public void ApplySizeToCharacter(string name, int w, int h) => throw Unsupported(nameof(ApplySizeToCharacter));
        public void UnmuteAllClientAudio() => throw Unsupported(nameof(UnmuteAllClientAudio));
        public void SetClientVolume(string characterName, int percent) => throw Unsupported(nameof(SetClientVolume));
        public void SetCharacterVisibility(string characterName, bool visible) => throw Unsupported(nameof(SetCharacterVisibility));
        public void CreateSecondaryForCharacter(string charName) => throw Unsupported(nameof(CreateSecondaryForCharacter));
        public void DestroySecondaryForCharacter(string charName) => throw Unsupported(nameof(DestroySecondaryForCharacter));
        public void UpdateCharacterLabel(string characterName, string? labelText, string? colorHex, int sizePt) => throw Unsupported(nameof(UpdateCharacterLabel));
        public void ForgetJumps(string name) => throw Unsupported(nameof(ForgetJumps));
        public bool ForgetPlaytime(string name) => throw Unsupported(nameof(ForgetPlaytime));
        public void ClearPlaytime() => throw Unsupported(nameof(ClearPlaytime));
        /// <summary>Render time (CharacterStores.IsInCycle runs while editors build rows): no character is excluded here.</summary>
        public bool IsExcludedFromCycle(string characterName) => false;
        /// <summary>No cycle exclusion on Linux (IsExcludedFromCycle is always false), so there is nothing to toggle: the
        /// same answer both ways, not a refusal one way and a no-op the other.</summary>
        public void ToggleCycleExclusion(string characterName) { }

        private static PlatformNotSupportedException Unsupported(string member) =>
            new($"ThumbnailManager.{member} is Windows-only (S3)");
    }

    public sealed class CropManager
    {
    }

    public sealed class HotkeyService
    {
    }
}

namespace EveMultiPreview.Views
{
    public sealed class ThumbnailWindow
    {
        public const int MaxCornerRadius = EveMultiPreview.Models.Profile.MaxCornerRadius;
    }
}
