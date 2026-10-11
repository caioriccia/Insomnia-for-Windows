// Editor de lista de executáveis (lista de congelados, lista de agentes): itens com botão
// remover, campo para digitar um nome e "Adicionar app em execução…". Identificador = nome do
// executável ("slack.exe"), comparado sem diferenciar caixa (desenho §4.3).
using System.Collections.Immutable;

namespace Insomnia.App.Settings;

public sealed class NameListEditor : UserControl
{
    private readonly ListBox _list = new() { Width = 300, Height = 110, IntegralHeight = false };
    private readonly TextBox _input = new() { Width = 200, PlaceholderText = "nome do executável (ex.: slack.exe)" };
    private readonly Button _add = new() { Text = "Adicionar", AutoSize = true, Enabled = false };
    private readonly Button _remove = new() { Text = "Remover", AutoSize = true, Enabled = false };
    private readonly Button _running = new() { Text = "Adicionar app em execução…", AutoSize = true };
    private readonly ContextMenuStrip _runningMenu = new() { ShowImageMargin = false };
    private readonly Func<Task<IReadOnlyList<string>>> _runningApps;
    private ImmutableArray<string> _items = [];

    public NameListEditor(string title, Func<Task<IReadOnlyList<string>>> runningApps)
    {
        _runningApps = runningApps;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var column = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        column.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 6, 0, 2) });
        column.Controls.Add(_list);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange([_input, _add, _remove]);
        column.Controls.Add(row);
        column.Controls.Add(_running);
        Controls.Add(column);

        _input.TextChanged += (_, _) => _add.Enabled = Normalize(_input.Text) is not null;
        _input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddTyped(); } };
        _add.Click += (_, _) => AddTyped();
        _list.SelectedIndexChanged += (_, _) => _remove.Enabled = _list.SelectedIndex >= 0;
        _remove.Click += (_, _) =>
        {
            if (_list.SelectedItem is string name) Commit(_items.RemoveAll(i => string.Equals(i, name, StringComparison.OrdinalIgnoreCase)));
        };
        _running.Click += async (_, _) => await ShowRunningAsync();
    }

    /// Lista mudou pela mão da pessoa (quem ouve grava no config).
    public event Action<ImmutableArray<string>>? ListChanged;

    public ImmutableArray<string> Items => _items;

    /// Carrega sem disparar ListChanged.
    public void SetItems(IEnumerable<string> items)
    {
        _items = [.. items];
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var item in _items) _list.Items.Add(item);
        _list.EndUpdate();
        _remove.Enabled = false;
    }

    /// "slack" ⇒ "slack.exe"; espaços nas pontas somem; vazio ou com separador de pasta ⇒ null.
    public static string? Normalize(string? text)
    {
        var name = text?.Trim().Trim('"') ?? "";
        if (name.Length == 0 || name.IndexOfAny(['\\', '/', ':']) >= 0) return null;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    private void AddTyped()
    {
        if (Normalize(_input.Text) is not { } name) return;
        AddName(name);
        _input.Clear();
    }

    private void AddName(string name)
    {
        if (_items.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        Commit(_items.Add(name));
    }

    private void Commit(ImmutableArray<string> next)
    {
        SetItems(next);
        ListChanged?.Invoke(next);
    }

    private async Task ShowRunningAsync()
    {
        _running.Enabled = false;
        IReadOnlyList<string> names;
        try { names = await _runningApps(); }
        catch (Exception) { names = []; }
        finally { if (!IsDisposed) _running.Enabled = true; }
        if (IsDisposed) return;

        foreach (ToolStripItem old in _runningMenu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        _runningMenu.Items.Clear();
        var candidates = names.Where(n => !_items.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
            _runningMenu.Items.Add(new ToolStripMenuItem("Nenhum outro app com janela aberto") { Enabled = false });
        foreach (var name in candidates)
        {
            var item = new ToolStripMenuItem(name);
            item.Click += (_, _) => AddName(name);
            _runningMenu.Items.Add(item);
        }
        _runningMenu.Show(_running, new Point(0, _running.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _runningMenu.Dispose();
        base.Dispose(disposing);
    }
}
