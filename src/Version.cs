using System.Reflection;

// Il numero di versione viene sostituito automaticamente da GitHub Actions quando si pubblica una release.
[assembly: AssemblyTitle("MineServer Manager")]
[assembly: AssemblyProduct("MineServer Manager")]
[assembly: AssemblyDescription("Server Minecraft per mini PC: Vanilla, Paper, Fabric, Forge")]
[assembly: AssemblyVersion("1.0.0")]
[assembly: AssemblyFileVersion("1.0.0")]

namespace MineServer
{
    public static class AppInfo
    {
        public const string Version = "1.0.0";
        public const string Repo = "P4ty123/Launcher-server-Mc";
        public const string RepoUrl = "https://github.com/" + Repo;
    }
}
