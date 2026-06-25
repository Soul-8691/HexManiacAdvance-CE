using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace HavenSoft.HexManiac.Core.ViewModels {
   public class TableGridTab : ViewModelCore, ITabContent {
      private readonly IFileSystem fs;
      private readonly ChangeHistory<ModelDelta> history;
      private readonly IDataModel model;
      private readonly ViewPort viewPort;
      private readonly StubCommand undo, redo;
      private StubCommand addSortRule, addFilterRule, removeSortRule, removeFilterRule;

      private ITableRun currentTable;

      public string TableName { get; }
      public IReadOnlyList<string> ColumnNames { get; private set; } = Array.Empty<string>();
      public ObservableCollection<GridRowViewModel> Rows { get; } = new();
      public ObservableCollection<SortRule> SortRules { get; } = new();
      public ObservableCollection<FilterRule> FilterRules { get; } = new();

      public event EventHandler ColumnsChanged;

      public ModelDelta Token => history.CurrentChange;
      public IDataModel Model => model;

      #region ITabContent

      public string Name => "Grid: " + TableName;
      public string FullFileName { get; }
      public bool SpartanMode { get; set; }
      public bool IsMetadataOnlyChange => false;

      public ICommand Save { get; }
      public ICommand SaveAs { get; }
      public ICommand ExportBackup { get; } = new StubCommand();
      public ICommand Undo => undo;
      public ICommand Redo => redo;
      public ICommand Copy { get; } = new StubCommand();
      public ICommand DeepCopy { get; } = new StubCommand();
      public ICommand Diff => null;
      public ICommand DiffLeft => null;
      public ICommand DiffRight => null;
      public ICommand Clear { get; } = new StubCommand();
      public ICommand SelectAll { get; } = new StubCommand();
      public ICommand Goto { get; } = new StubCommand();
      public ICommand ResetAlignment { get; } = new StubCommand();
      public ICommand Back { get; } = new StubCommand();
      public ICommand Forward { get; } = new StubCommand();
      public ICommand Close { get; }
      public bool CanDuplicate => false;
      public void Duplicate() { }
      public bool CanIpsPatchRight => false;
      public bool CanUpsPatchRight => false;
      public void IpsPatchRight() { }
      public void UpsPatchRight() { }

      public event EventHandler<string> OnError;
      public event EventHandler Closed;
      public event EventHandler<TabChangeRequestedEventArgs> RequestTabChange;
      event EventHandler<string> ITabContent.OnMessage { add { } remove { } }
      event EventHandler ITabContent.ClearMessage { add { } remove { } }
      event EventHandler<Action> ITabContent.RequestDelayedWork { add { } remove { } }
      event EventHandler ITabContent.RequestMenuClose { add { } remove { } }
      event EventHandler<Direction> ITabContent.RequestDiff { add { } remove { } }
      event EventHandler<CanDiffEventArgs> ITabContent.RequestCanDiff { add { } remove { } }
      event EventHandler<CanPatchEventArgs> ITabContent.RequestCanCreatePatch { add { } remove { } }
      event EventHandler<CanPatchEventArgs> ITabContent.RequestCreatePatch { add { } remove { } }
      event EventHandler ITabContent.RequestRefreshGotoShortcuts { add { } remove { } }

      public bool TryImport(LoadedFile file, IFileSystem fileSystem) => false;

      #endregion

      public ICommand AddSortRule => StubCommand(ref addSortRule, () => SortRules.Add(new SortRule(ColumnNames)));
      public ICommand AddFilterRule => StubCommand(ref addFilterRule, () => FilterRules.Add(new FilterRule(ColumnNames)));
      public ICommand RemoveSortRule => StubCommand<SortRule>(ref removeSortRule, rule => SortRules.Remove(rule));
      public ICommand RemoveFilterRule => StubCommand<FilterRule>(ref removeFilterRule, rule => FilterRules.Remove(rule));

      public TableGridTab(IFileSystem fileSystem, ViewPort viewPort, string tableName) {
         fs = fileSystem;
         this.viewPort = viewPort;
         history = viewPort.ChangeHistory;
         model = viewPort.Model;
         TableName = tableName;
         FullFileName = viewPort.Name;
         Save = viewPort.Save;
         SaveAs = viewPort.SaveAs;

         undo = new StubCommand {
            CanExecute = history.Undo.CanExecute,
            Execute = arg => { history.Undo.Execute(arg); Refresh(); },
         };
         history.Undo.CanExecuteChanged += UndoCanExecuteWatcher;

         redo = new StubCommand {
            CanExecute = history.Redo.CanExecute,
            Execute = arg => { history.Redo.Execute(arg); Refresh(); },
         };
         history.Redo.CanExecuteChanged += RedoCanExecuteWatcher;

         Close = new StubCommand {
            CanExecute = arg => true,
            Execute = arg => {
               Closed?.Invoke(this, EventArgs.Empty);
               history.Undo.CanExecuteChanged -= UndoCanExecuteWatcher;
               history.Redo.CanExecuteChanged -= RedoCanExecuteWatcher;
            },
         };

         SortRules.CollectionChanged += SortRulesChanged;
         FilterRules.CollectionChanged += FilterRulesChanged;

         Refresh();
      }

      public void Refresh() {
         history.ChangeCompleted();
         currentTable = (ITableRun)model.GetNextRun(model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, TableName));
         ColumnNames = currentTable.ElementContent
            .Where(IsSupportedColumn)
            .Select(seg => seg.Name)
            .ToList();
         NotifyPropertyChanged(nameof(ColumnNames));
         ColumnsChanged?.Invoke(this, EventArgs.Empty);
         RecomputeRows();
      }

      public void RecomputeRows() {
         IEnumerable<GridRowViewModel> source = Enumerable.Range(0, currentTable.ElementCount)
            .Select(i => new GridRowViewModel(this, i));

         var activeFilters = FilterRules.Where(f => !string.IsNullOrEmpty(f.ColumnName)).ToList();
         if (activeFilters.Count > 0) {
            source = source.Where(row => EvaluateFilterChain(activeFilters, row));
         }

         var sortRules = SortRules.Where(r => !string.IsNullOrEmpty(r.ColumnName)).ToList();
         if (sortRules.Count > 0) {
            var ordered = sortRules[0].Direction == SortDirection.Ascending
               ? source.OrderBy(row => SortKey(row, sortRules[0].ColumnName))
               : source.OrderByDescending(row => SortKey(row, sortRules[0].ColumnName));
            for (int i = 1; i < sortRules.Count; i++) {
               var rule = sortRules[i];
               ordered = rule.Direction == SortDirection.Ascending
                  ? ordered.ThenBy(row => SortKey(row, rule.ColumnName))
                  : ordered.ThenByDescending(row => SortKey(row, rule.ColumnName));
            }
            source = ordered;
         }

         var rows = source.ToList();
         Rows.Clear();
         foreach (var row in rows) Rows.Add(row);
      }

      // Evaluated strictly left-to-right (no AND-before-OR precedence grouping): each rule's own
      // CombineMode says how it joins with everything accumulated so far. The first rule has
      // nothing before it to combine with, so its CombineMode is ignored.
      private static bool EvaluateFilterChain(IReadOnlyList<FilterRule> filters, GridRowViewModel row) {
         var result = filters[0].Matches(row);
         for (int i = 1; i < filters.Count; i++) {
            var matches = filters[i].Matches(row);
            result = filters[i].CombineMode == FilterCombineMode.Or ? (result || matches) : (result && matches);
         }
         return result;
      }

      private IComparable SortKey(GridRowViewModel row, string column) {
         var text = row[column]?.ToString() ?? string.Empty;
         if (IsNumericColumn(column) && double.TryParse(text, out var numeric)) return numeric;
         return text;
      }

      public void GotoRow(GridRowViewModel row) {
         viewPort.Goto.Execute(row.Address.ToString("X6"));
         var args = new TabChangeRequestedEventArgs(viewPort);
         RequestTabChange?.Invoke(this, args);
      }

      public bool IsEnumColumn(string columnName) => FindSegment(columnName) is ArrayRunEnumSegment;

      // Enum/PCS/Pointer-anchor text should sort alphabetically; only plain Integer/Signed/Hex
      // segments (whose Serialize() happens to come out as a decimal number) should sort numerically.
      public bool IsNumericColumn(string columnName) {
         var seg = FindSegment(columnName);
         if (seg == null || seg is ArrayRunEnumSegment) return false;
         return seg.Type == ElementContentType.Integer;
      }

      // Sparse [[List]] entries in the .toml leave gap indices as null (filled in only when
      // displaying an existing value, via the ToText/Serialize "?? index.ToString()" fallback) -
      // those gaps aren't real named options, so they're excluded from the dropdown's choices.
      public IReadOnlyList<string> GetEnumOptions(string columnName) {
         var seg = FindSegment(columnName) as ArrayRunEnumSegment;
         if (seg == null) return Array.Empty<string>();
         return seg.GetOptions(model).Where(o => o != null).ToList();
      }

      public bool IsEditableColumn(string columnName) {
         var seg = FindSegment(columnName);
         if (seg == null) return false;
         if (seg is ArrayRunTupleSegment) return false; // packed bits: not a single editable value
         if (seg is ArrayRunEnumSegment) return true;
         if (seg.Type == ElementContentType.PCS) return true;
         if (seg.Type == ElementContentType.Integer) return true; // covers plain/signed/hex segments
         return false; // Pointer, BitArray, and anything exotic stay read-only
      }

      public object ReadDisplayValue(int arrayIndex, string columnName) => GetElement(arrayIndex).Serialize(columnName);

      public void WriteDisplayValue(int arrayIndex, string columnName, object newValue) {
         var seg = FindSegment(columnName);
         if (seg == null) return;
         var element = GetElement(arrayIndex);
         var text = newValue?.ToString() ?? string.Empty;
         try {
            if (seg is ArrayRunEnumSegment) {
               element.SetEnumValue(columnName, text);
            } else if (seg.Type == ElementContentType.PCS) {
               element.SetStringValue(columnName, text);
            } else if (seg.Type == ElementContentType.Integer) {
               int parsed;
               if ((text.StartsWith("0x") || text.StartsWith("0X")) && text.Substring(2).TryParseHex(out parsed)) {
                  // parsed via hex
               } else if (!int.TryParse(text, out parsed)) {
                  throw new FormatException($"'{text}' is not a valid number.");
               }
               element.SetValue(columnName, parsed);
            } else {
               return; // read-only column; nothing to write
            }
            history.ChangeCompleted();
         } catch (Exception ex) {
            OnError?.Invoke(this, $"Could not set {columnName}: {ex.Message}");
         }
      }

      private ArrayRunElementSegment FindSegment(string columnName) => currentTable.ElementContent.FirstOrDefault(s => s.Name == columnName);

      private ModelArrayElement GetElement(int arrayIndex) => new(model, currentTable.Start, arrayIndex, () => history.CurrentChange, currentTable);

      private static bool IsSupportedColumn(ArrayRunElementSegment seg) =>
         !(seg is ArrayRunRecordSegment) && seg.Type != ElementContentType.Splitter && seg.Length > 0;

      private void SortRulesChanged(object sender, NotifyCollectionChangedEventArgs e) {
         if (e.NewItems != null) foreach (SortRule rule in e.NewItems) rule.PropertyChanged += RulePropertyChanged;
         if (e.OldItems != null) foreach (SortRule rule in e.OldItems) rule.PropertyChanged -= RulePropertyChanged;
         RecomputeRows();
      }

      private void FilterRulesChanged(object sender, NotifyCollectionChangedEventArgs e) {
         if (e.NewItems != null) foreach (FilterRule rule in e.NewItems) rule.PropertyChanged += RulePropertyChanged;
         if (e.OldItems != null) foreach (FilterRule rule in e.OldItems) rule.PropertyChanged -= RulePropertyChanged;
         RecomputeRows();
      }

      private void RulePropertyChanged(object sender, PropertyChangedEventArgs e) => RecomputeRows();

      private void UndoCanExecuteWatcher(object sender, EventArgs e) => undo.CanExecuteChanged.Invoke(undo, EventArgs.Empty);
      private void RedoCanExecuteWatcher(object sender, EventArgs e) => redo.CanExecuteChanged.Invoke(redo, EventArgs.Empty);
   }

   public class GridRowViewModel : ViewModelCore {
      private readonly TableGridTab owner;

      public int ArrayIndex { get; }
      public int Address { get; }
      public string RowName { get; }

      public GridRowViewModel(TableGridTab owner, int arrayIndex) {
         this.owner = owner;
         ArrayIndex = arrayIndex;
         var table = owner.Model.GetNextRun(owner.Model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, owner.TableName)) as ITableRun;
         Address = (table?.Start ?? 0) + (table?.ElementLength ?? 0) * arrayIndex;
         var names = table?.ElementNames;
         RowName = (names != null && arrayIndex < names.Count) ? names[arrayIndex] : arrayIndex.ToString();
      }

      public object this[string columnName] {
         get => owner.ReadDisplayValue(ArrayIndex, columnName);
         set => owner.WriteDisplayValue(ArrayIndex, columnName, value);
      }
   }

   public enum SortDirection { Ascending, Descending }

   public class SortRule : ViewModelCore {
      public IReadOnlyList<string> AvailableColumns { get; }
      public IReadOnlyList<string> AvailableDirections { get; } = new[] { "Ascending", "Descending" };

      private string columnName;
      public string ColumnName { get => columnName; set => Set(ref columnName, value); }

      private string directionText = "Ascending";
      public string DirectionText { get => directionText; set => Set(ref directionText, value); }

      public SortDirection Direction => DirectionText == "Descending" ? SortDirection.Descending : SortDirection.Ascending;

      public SortRule(IReadOnlyList<string> availableColumns) {
         AvailableColumns = availableColumns;
         columnName = availableColumns.FirstOrDefault();
      }
   }

   public enum FilterOperator { Equals, Contains, GreaterThan, LessThan }

   public enum FilterCombineMode { And, Or }

   public class FilterRule : ViewModelCore {
      public IReadOnlyList<string> AvailableColumns { get; }
      public IReadOnlyList<string> AvailableOperators { get; } = new[] { "Equals", "Contains", "GreaterThan", "LessThan" };
      public IReadOnlyList<string> AvailableCombineModes { get; } = new[] { "AND", "OR" };

      // How this rule joins with everything before it in the chain. Meaningless for the first
      // rule in the list (nothing precedes it), but every rule still carries one so that moving
      // rules around / adding new ones ahead of it doesn't lose the setting.
      private string combineModeText = "AND";
      public string CombineModeText { get => combineModeText; set => Set(ref combineModeText, value); }
      public FilterCombineMode CombineMode => CombineModeText == "OR" ? FilterCombineMode.Or : FilterCombineMode.And;

      private string columnName;
      public string ColumnName { get => columnName; set => Set(ref columnName, value); }

      private string operatorText = "Equals";
      public string OperatorText { get => operatorText; set => Set(ref operatorText, value); }

      private string value = string.Empty;
      public string Value { get => value; set => Set(ref this.value, value); }

      public FilterRule(IReadOnlyList<string> availableColumns) {
         AvailableColumns = availableColumns;
         columnName = availableColumns.FirstOrDefault();
      }

      public bool Matches(GridRowViewModel row) {
         if (string.IsNullOrEmpty(ColumnName)) return true;
         var raw = row[ColumnName];
         var cell = raw?.ToString() ?? string.Empty;
         switch (OperatorText) {
            case "Equals": return cell.Equals(Value, StringComparison.OrdinalIgnoreCase);
            case "Contains": return !string.IsNullOrEmpty(Value) && cell.IndexOf(Value, StringComparison.OrdinalIgnoreCase) >= 0;
            case "GreaterThan": return double.TryParse(cell, out var g) && double.TryParse(Value, out var gv) && g > gv;
            case "LessThan": return double.TryParse(cell, out var l) && double.TryParse(Value, out var lv) && l < lv;
            default: return true;
         }
      }
   }
}
