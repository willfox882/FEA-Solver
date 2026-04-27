using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FEASolver.Core.Models;
using System.Collections.ObjectModel;

namespace FEASolver.ViewModels;

public class ModelTreeNode
{
    public string Label   { get; set; } = "";
    public string Icon    { get; set; } = "";
    public object? Tag    { get; set; }
    public ObservableCollection<ModelTreeNode> Children { get; } = new();
}

public partial class ModelTreeViewModel : ObservableObject
{
    // ── Public events ─────────────────────────────────────────────────────────

    /// <summary>Raised when the user requests deletion of a BC or Load from the tree.</summary>
    public event Action<BoundaryCondition>? DeleteBcRequested;
    /// <summary>Raised when the user requests deletion of a Load from the tree.</summary>
    public event Action<Load>? DeleteLoadRequested;
    /// <summary>Raised when the user selects a BC/Load node — highlights the face.</summary>
    public event Action<int>? FaceHighlightRequested;

    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<ModelTreeNode> _rootNodes = new();

    [ObservableProperty]
    private ModelTreeNode? _selectedNode;

    // ── Private tree structure refs ───────────────────────────────────────────

    private ModelTreeNode? _bcParent;
    private ModelTreeNode? _loadParent;

    // ── Mesh load ─────────────────────────────────────────────────────────────

    public void LoadMesh(MeshData meshData)
    {
        RootNodes.Clear();

        var model = new ModelTreeNode { Label = "Model", Icon = "📦" };

        var geometry = new ModelTreeNode { Label = "Geometry", Icon = "🔷" };
        geometry.Children.Add(new ModelTreeNode
        {
            Label = $"Mesh ({meshData.Nodes.Length:N0} nodes, " +
                    $"{meshData.Elements.Length:N0} elements)",
            Icon = "▦"
        });
        foreach (var face in meshData.FaceGroups)
            geometry.Children.Add(new ModelTreeNode
            {
                Label = $"Face {face.FaceId}  ({face.NodeIds.Length} nodes)",
                Icon  = "◻",
                Tag   = face.FaceId   // int → viewport selection
            });

        _bcParent   = new ModelTreeNode { Label = "Boundary Conditions", Icon = "🔒" };
        _loadParent = new ModelTreeNode { Label = "Loads",                Icon = "→"  };

        model.Children.Add(geometry);
        model.Children.Add(_bcParent);
        model.Children.Add(_loadParent);
        model.Children.Add(new ModelTreeNode { Label = "Material", Icon = "🧱" });
        model.Children.Add(new ModelTreeNode { Label = "Results",  Icon = "📊" });

        RootNodes.Add(model);
    }

    // ── Sync helpers (also called by MainViewModel directly) ─────────────────

    public void SyncBoundaryConditions(IEnumerable<BoundaryCondition> bcs)
    {
        if (_bcParent == null) return;
        _bcParent.Children.Clear();
        foreach (var bc in bcs)
        {
            string nodeInfo = bc.NodeIds.Length > 0
                ? $"{bc.NodeIds.Length} nodes"
                : "(unresolved)";
            _bcParent.Children.Add(new ModelTreeNode
            {
                Label = $"{bc.Type}  —  Face {bc.FaceId}  [{nodeInfo}]",
                Icon  = "🔒",
                Tag   = bc
            });
        }
    }

    public void SyncLoads(IEnumerable<Load> loads)
    {
        if (_loadParent == null) return;
        _loadParent.Children.Clear();
        foreach (var load in loads)
        {
            string detail = load.Type switch
            {
                LoadType.PointLoad when load.Localized?.Force is { Length: >= 3 } f =>
                    $"F=({f[0]:G3}, {f[1]:G3}, {f[2]:G3}) N" +
                    (load.Localized.ResolvedNodeId is int n ? $" @ node {n}" : ""),
                LoadType.Moment or LoadType.Torque =>
                    $"M={load.Magnitude:G4} N·m",
                LoadType.Pressure =>
                    $"P={load.Magnitude:G4} Pa",
                LoadType.SurfaceTraction =>
                    $"t={load.Magnitude:G4} Pa",
                _ => $"{load.Magnitude:G4}"
            };
            _loadParent.Children.Add(new ModelTreeNode
            {
                Label = $"{load.Type}  —  Face {load.FaceId}  [{detail}]",
                Icon  = "→",
                Tag   = load
            });
        }
    }


    // ── Selection ─────────────────────────────────────────────────────────────

    partial void OnSelectedNodeChanged(ModelTreeNode? value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        if (value == null) return;

        int faceId = -1;
        if (value.Tag is int id)
            faceId = id;
        else if (value.Tag is BoundaryCondition bc)
            faceId = bc.FaceId;
        else if (value.Tag is Load ld)
            faceId = ld.FaceId;

        if (faceId >= 0)
            FaceHighlightRequested?.Invoke(faceId);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (SelectedNode?.Tag is BoundaryCondition bc)
            DeleteBcRequested?.Invoke(bc);
        else if (SelectedNode?.Tag is Load load)
            DeleteLoadRequested?.Invoke(load);
    }

    private bool CanDeleteSelected() =>
        SelectedNode?.Tag is BoundaryCondition ||
        SelectedNode?.Tag is Load;
}
