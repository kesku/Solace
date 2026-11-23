namespace Solace.StaticData;

public sealed class Buildplates
{
    private const string ShopDirectory = "shop";
    private const string LevelDirectory = "level";

    private readonly string _directory;

    internal Buildplates(string dir)
    {
        _directory = dir;

        Directory.CreateDirectory(Path.Combine(_directory, ShopDirectory));
        Directory.CreateDirectory(Path.Combine(_directory, LevelDirectory));
    }

    public IEnumerable<StaticBuidplate> ShopBuildplates => Directory.EnumerateFiles(Path.Combine(_directory, ShopDirectory))
        .Select(path => new StaticBuidplate(path));

    // older static data named these level_<n>.zip, skip anything not named by its template id
    public IEnumerable<StaticBuidplate> LevelBuildplates => Directory.EnumerateFiles(Path.Combine(_directory, LevelDirectory))
        .Where(path => Guid.TryParse(Path.GetFileNameWithoutExtension(path), out _))
        .Select(path => new StaticBuidplate(path));
}
