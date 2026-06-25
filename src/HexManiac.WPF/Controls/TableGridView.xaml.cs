using HavenSoft.HexManiac.Core.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace HavenSoft.HexManiac.WPF.Controls {
   public partial class TableGridView {
      private TableGridTab ViewModel => DataContext as TableGridTab;

      public TableGridView() {
         InitializeComponent();
         DataContextChanged += OnDataContextChanged;
      }

      private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) {
         if (e.OldValue is TableGridTab oldTab) oldTab.ColumnsChanged -= BuildColumns;
         if (e.NewValue is TableGridTab newTab) {
            newTab.ColumnsChanged += BuildColumns;
            BuildColumns(newTab, EventArgs.Empty);
         }
      }

      private void BuildColumns(object sender, EventArgs e) {
         var tab = (TableGridTab)sender;
         RowsGrid.Columns.Clear();
         RowsGrid.Columns.Add(new DataGridTextColumn {
            Header = "Row",
            Binding = new Binding(nameof(GridRowViewModel.RowName)) { Mode = BindingMode.OneWay },
            IsReadOnly = true,
         });
         var comboEditStyle = (Style)FindResource("GridComboEditStyle");
         foreach (var columnName in tab.ColumnNames) {
            if (tab.IsEnumColumn(columnName)) {
               RowsGrid.Columns.Add(new DataGridComboBoxColumn {
                  Header = columnName,
                  ItemsSource = tab.GetEnumOptions(columnName),
                  SelectedItemBinding = new Binding($"[{columnName}]"),
                  IsReadOnly = false,
                  EditingElementStyle = comboEditStyle,
               });
            } else {
               RowsGrid.Columns.Add(new DataGridTextColumn {
                  Header = columnName,
                  Binding = new Binding($"[{columnName}]"),
                  IsReadOnly = !tab.IsEditableColumn(columnName),
               });
            }
         }
      }

      private void RowDoubleClick(object sender, MouseButtonEventArgs e) {
         // Only the read-only "Row" column double-click navigates to the main view - any other
         // column's double-click is the normal WPF gesture to begin editing that cell (including
         // opening a dropdown for enum columns), and must not be hijacked by tab navigation.
         var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
         if (!(cell?.Column?.Header is string header) || header != "Row") return;
         var row = FindAncestor<DataGridRow>(cell);
         if (row?.Item is GridRowViewModel gridRow) {
            ViewModel?.GotoRow(gridRow);
            e.Handled = true;
         }
      }

      private void CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) {
         // Defer until after the binding commit finishes, so RecomputeRows (which replaces the
         // Rows collection) doesn't run while the DataGrid is still mid-commit of this edit.
         Dispatcher.BeginInvoke(new Action(() => ViewModel?.RecomputeRows()));
      }

      private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject {
         while (node != null && !(node is T)) node = VisualTreeHelper.GetParent(node);
         return node as T;
      }
   }
}
