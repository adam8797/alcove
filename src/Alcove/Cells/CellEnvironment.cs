using Alcove.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcove.Cells;

/// <summary>
/// What the cells of one plugin host share: the host inventory, the lock under which answers are frozen
/// (shared with the install pipeline), the cells by name (for delegation), the shared WPF-relevant
/// assemblies, the native library registry and the "cell created" callbacks. One per plugin host; no static state.
/// </summary>
internal sealed class CellEnvironment
{
    private readonly Dictionary<string, Cell> _cells = new(StringComparer.Ordinal);
    private readonly List<Action<Cell>> _cellCreated = [];

    /// <summary>Creates the environment.</summary>
    public CellEnvironment(IHostAssemblies host, ILoggerFactory? loggerFactory = null, SharedAssemblyRegistry? sharedAssemblies = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        SharedAssemblies = sharedAssemblies ?? new SharedAssemblyRegistry();
        Logger = LoggerFactory.CreateLogger("Alcove.Cells");
    }

    /// <summary>
    /// The one lock under which every cell freezes its answers. The install pipeline takes it too while it
    /// compares and extends cells' plans, so a hot install can't race with a load.
    /// </summary>
    public Lock Gate { get; } = new();

    /// <summary>The host inventory, for names outside a cell's plan.</summary>
    public IHostAssemblies Host { get; }

    /// <summary>The logger factory.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>Identical WPF-relevant private assemblies loaded once for all cells.</summary>
    public SharedAssemblyRegistry SharedAssemblies { get; }

    /// <summary>Native libraries loaded by the cells, for soname collision warnings.</summary>
    public NativeLibraryRegistry NativeLibraries { get; } = new();

    internal ILogger Logger { get; }

    /// <summary>
    /// Test hook: runs in a cell's <c>Load</c>/<c>LoadUnmanagedDll</c> right before the actual load, outside
    /// <see cref="Gate"/>, with the requested name.
    /// </summary>
    internal Action<Cell, string>? LoadingHook { get; set; }

    /// <summary>The cells created so far.</summary>
    public IReadOnlyList<Cell> Cells
    {
        get
        {
            lock (Gate)
            {
                return _cells.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Adds a callback run right after each cell is created, before anything loads into it, e.g. the WPF
    /// add-on's pre-loading of WPF-relevant assemblies.
    /// </summary>
    public void AddCellCreatedCallback(Action<Cell> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (Gate)
        {
            _cellCreated.Add(callback);
        }
    }

    /// <summary>Creates a cell, registers it by name and runs the "cell created" callbacks.</summary>
    /// <exception cref="InvalidOperationException">A cell with this name already exists.</exception>
    public Cell CreateCell(string name, CellPlan plan)
    {
        var cell = RegisterCell(name, plan);
        RunCellCreated(cell);
        return cell;
    }

    /// <summary>
    /// Creates a cell and registers it by name, without running the "cell created" callbacks (they may load
    /// assemblies, so they must not run under <see cref="Gate"/>) or logging. The caller runs <see cref="RunCellCreated"/> next.
    /// </summary>
    /// <exception cref="InvalidOperationException">A cell with this name already exists.</exception>
    public Cell RegisterCell(string name, CellPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(plan);
        Cell cell;
        lock (Gate)
        {
            if (_cells.ContainsKey(name))
            {
                throw new InvalidOperationException($"A cell named '{name}' already exists.");
            }

            cell = new Cell(name, plan, this);
            _cells[name] = cell;
        }

        return cell;
    }

    /// <summary>Runs the "cell created" callbacks for a cell registered with <see cref="RegisterCell"/>. Call it outside <see cref="Gate"/>.</summary>
    public void RunCellCreated(Cell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        CellLog.CellCreated(Logger, cell.Name, cell.Plugins.Count);   // here, not in RegisterCell: callers may hold the Gate there
        List<Action<Cell>> callbacks;
        lock (Gate)
        {
            callbacks = _cellCreated.ToList();
        }

        foreach (var callback in callbacks)
        {
            callback(cell);
        }
    }

    /// <summary>
    /// Creates one cell per classification. Cells refer to their providers by name, so the order doesn't
    /// matter, and nothing loads until a plugin assembly is loaded into a cell.
    /// </summary>
    public IReadOnlyList<Cell> CreateCells(IEnumerable<CellClassification> classifications)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        return classifications.Select(c => CreateCell(c.Cell, c.Plan)).ToList();
    }

    /// <summary>The cell with this name, or <see langword="null"/>.</summary>
    public Cell? FindCell(string name)
    {
        lock (Gate)
        {
            return _cells.GetValueOrDefault(name);
        }
    }
}
