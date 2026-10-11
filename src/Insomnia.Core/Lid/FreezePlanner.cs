// O que congelar ao fechar a tampa (desenho §7.2). Puro: recebe config e snapshot,
// devolve grupos. Quem sinaliza é LidActions, que ainda confere cada pid com ProcessControl.
using System.Collections.Immutable;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;

namespace Insomnia.Core.Lid;

/// Um app a congelar: as instâncias do executável e seus descendentes. Explicit = veio da
/// FreezeList (e não do modo "congelar os outros apps").
public sealed record FreezeGroup(string Name, ImmutableArray<ProcessInfo> Members, bool Explicit);

/// Quem é "o próprio app" e onde fica o Windows. Separado para os testes não dependerem da máquina.
public sealed record PlannerEnvironment(int OwnPid, string OwnExeName, string? WindowsDirectory)
{
    public static PlannerEnvironment Current { get; } = new(
        Environment.ProcessId,
        Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } exe ? exe : "Insomnia.exe",
        Environment.GetEnvironmentVariable("WINDIR"));
}

public static class FreezePlanner
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    /// Nunca congelados, nem pela lista explícita: o shell, o compositor e o próprio Insomnia.
    /// (Tudo sob %WINDIR% também, conferido pelo caminho.)
    public static readonly ImmutableHashSet<string> SystemDenied =
        ImmutableHashSet.Create(Names, "Insomnia.exe", "explorer.exe", "dwm.exe");

    /// Fora do planejador comum: a regra Docker (DockerRule) é a dona dessa árvore.
    public static readonly ImmutableHashSet<string> DockerOwned =
        ImmutableHashSet.Create(Names, "Docker Desktop.exe", "com.docker.backend.exe");

    /// Protegidos só do modo automático (FreezeAllApps). Pôr um deles na FreezeList à mão
    /// ainda congela — como no original.
    public static readonly ImmutableHashSet<string> BuiltInProtected = ImmutableHashSet.Create(Names,
        // navegadores
        "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "opera.exe", "vivaldi.exe", "Arc.exe", "zen.exe", "chromium.exe",
        // terminais
        "WindowsTerminal.exe", "wt.exe", "OpenConsole.exe", "conhost.exe", "cmd.exe", "pwsh.exe", "powershell.exe",
        "alacritty.exe", "wezterm-gui.exe", "Tabby.exe", "Hyper.exe", "warp.exe", "mintty.exe",
        // editores e IDEs
        "Code.exe", "Code - Insiders.exe", "Cursor.exe", "Zed.exe", "Windsurf.exe", "Antigravity.exe", "devenv.exe",
        "sublime_text.exe", "notepad++.exe", "idea64.exe", "pycharm64.exe", "rider64.exe", "webstorm64.exe",
        "goland64.exe", "clion64.exe", "phpstorm64.exe", "rubymine64.exe", "datagrip64.exe", "studio64.exe",
        // agentes e modelos locais
        "claude.exe", "ChatGPT.exe", "Codex.exe", "ollama.exe", "ollama app.exe", "LM Studio.exe", "node.exe", "python.exe",
        // reunião, gravação e ditado
        "Zoom.exe", "Teams.exe", "ms-teams.exe", "Webex.exe", "CiscoCollabHost.exe", "obs64.exe", "Loom.exe",
        "Granola.exe", "Wispr Flow.exe", "Otter.exe",
        // VPN e senhas
        "tailscaled.exe", "tailscale-ipn.exe", "1Password.exe", "Bitwarden.exe", "KeePassXC.exe",
        // WSL e serviços locais
        "wsl.exe", "wslhost.exe", "wslservice.exe", "postgres.exe");

    public static IReadOnlyList<FreezeGroup> Plan(Config config, IReadOnlyList<ProcessInfo> snapshot) =>
        Plan(config, snapshot, PlannerEnvironment.Current);

    /// Explícitos primeiro (FreezeList, na ordem do config), depois os automáticos
    /// (FreezeAllApps). Um pid entra em no máximo um grupo. Grupo sem membros não é devolvido.
    public static IReadOnlyList<FreezeGroup> Plan(Config config, IReadOnlyList<ProcessInfo> snapshot, PlannerEnvironment env)
    {
        var taken = new HashSet<int>();
        var groups = new List<FreezeGroup>();
        bool Denied(ProcessInfo p) => IsDenied(p, config, env);

        foreach (var name in config.FreezeList.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(Names))
        {
            var roots = snapshot.Where(p => Names.Equals(p.Name, name) && !Denied(p));
            var members = Tree(roots, snapshot, Denied, env, taken);
            if (members.Length > 0) groups.Add(new FreezeGroup(name, members, Explicit: true));
        }

        if (config.FreezeAllApps)
        {
            foreach (var name in AutomaticCandidates(config, snapshot, env))
            {
                var roots = snapshot.Where(p => Names.Equals(p.Name, name) && !Denied(p));
                var members = Tree(roots, snapshot, Denied, env, taken);
                if (members.Length > 0) groups.Add(new FreezeGroup(name, members, Explicit: false));
            }
        }
        return groups;
    }

    /// Nomes que o modo "congelar os outros apps" pegaria agora (também serve à tela de
    /// configurações, "congelaria agora: …"): processos com janela visível, fora da denylist,
    /// fora dos protegidos embutidos e fora da lista explícita. Sem repetição, na ordem do snapshot.
    public static IReadOnlyList<string> AutomaticCandidates(Config config, IReadOnlyList<ProcessInfo> snapshot, PlannerEnvironment env)
    {
        var explicitNames = new HashSet<string>(config.FreezeList, Names);
        var seen = new HashSet<string>(Names);
        var result = new List<string>();
        foreach (var p in snapshot)
        {
            if (!p.HasVisibleWindow || string.IsNullOrEmpty(p.Name)) continue;
            if (IsDenied(p, config, env) || BuiltInProtected.Contains(p.Name) || explicitNames.Contains(p.Name)) continue;
            if (seen.Add(p.Name)) result.Add(p.Name);
        }
        return result;
    }

    /// Denylist completa: sistema (próprio app, shell, %WINDIR%), árvore do Docker (dona: DockerRule)
    /// e AgentList. Vale para a lista explícita também — AgentList vence FreezeList.
    public static bool IsDenied(ProcessInfo p, Config config, PlannerEnvironment env) =>
        IsSystemDenied(p, env) || DockerOwned.Contains(p.Name) || config.AgentList.Contains(p.Name, Names);

    /// Só a parte de sistema da denylist (usada também pela regra Docker nos descendentes).
    public static bool IsSystemDenied(ProcessInfo p, PlannerEnvironment env)
    {
        if (p.Pid == env.OwnPid || p.Pid <= 4) return true;
        if (SystemDenied.Contains(p.Name) || Names.Equals(p.Name, env.OwnExeName)) return true;
        return IsUnderWindows(p.ExecutablePath, env.WindowsDirectory);
    }

    /// Raízes + descendentes (por ppid), em largura. Nó negado corta a subárvore; nó já
    /// parado fica de fora (nunca é nosso) mas os filhos dele seguem. Filho só conta se foi
    /// criado depois do pai — senão o ppid aponta para um pid reusado. Pids já tomados por
    /// outro grupo não entram de novo.
    public static ImmutableArray<ProcessInfo> Tree(IEnumerable<ProcessInfo> roots, IReadOnlyList<ProcessInfo> snapshot,
        Func<ProcessInfo, bool> denied, PlannerEnvironment env, HashSet<int> taken)
    {
        var children = snapshot.Where(p => p.Pid != p.ParentPid).ToLookup(p => p.ParentPid);
        var visited = new HashSet<int>();
        var queue = new Queue<ProcessInfo>();
        var result = ImmutableArray.CreateBuilder<ProcessInfo>();
        foreach (var r in roots.OrderBy(r => r.Pid))
            if (visited.Add(r.Pid)) queue.Enqueue(r);

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (!p.IsSuspended && taken.Add(p.Pid)) result.Add(p);
            foreach (var c in children[p.Pid].OrderBy(c => c.Pid))
            {
                if (visited.Contains(c.Pid) || denied(c) || !BornAfter(c, p)) continue;
                visited.Add(c.Pid);
                queue.Enqueue(c);
            }
        }
        return result.ToImmutable();
    }

    private static bool BornAfter(ProcessInfo child, ProcessInfo parent) =>
        child.Identity is { } c && parent.Identity is { } p && c.CreationTime >= p.CreationTime;

    private static bool IsUnderWindows(string? path, string? windowsDir)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(windowsDir)) return false;
        var root = windowsDir.TrimEnd('\\', '/') + "\\";
        return path.Replace('/', '\\').StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
