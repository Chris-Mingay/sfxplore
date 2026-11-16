namespace SoundShelf.Core;

public static class MixEditing
{
    // The edited clip wins. Preserve a victim's prefix, or its suffix when it
    // starts inside the edited interval. Never split or shift unrelated clips.
    public static void Place(MixProject mix, Guid trackId, MixClip clip)
    {
        var destination = mix.Tracks.First(t => t.Id == trackId);
        foreach (var track in mix.Tracks) track.Clips.RemoveAll(c => c.Id == clip.Id);
        clip.Start = Math.Max(0, clip.Start);
        foreach (var other in destination.Clips.ToArray())
        {
            if (other.End <= clip.Start || other.Start >= clip.End) continue;
            if (other.Start < clip.Start)
                other.TrimEnd = other.TrimStart + (clip.Start - other.Start) * other.Rate;
            else if (other.End > clip.End)
            {
                other.TrimStart += (clip.End - other.Start) * other.Rate;
                other.Start = clip.End;
            }
            else destination.Clips.Remove(other);
        }
        destination.Clips.Add(clip);
        destination.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}
