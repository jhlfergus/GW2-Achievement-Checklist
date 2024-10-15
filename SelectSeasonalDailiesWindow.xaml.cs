using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace GuildWars2Achievements
{
    public partial class SelectSeasonalDailiesWindow : Window
    {
        public List<int> SelectedCategoryIds { get; private set; } = new List<int>();
        private List<AchievementCategory> seasonalDailyCategories;

        public SelectSeasonalDailiesWindow(List<AchievementCategory> seasonalDailyCategories, List<int> selectedCategoryIds)
        {
            InitializeComponent();

            this.seasonalDailyCategories = seasonalDailyCategories;

            // Create checkboxes for each seasonal daily category
            foreach (var category in seasonalDailyCategories)
            {
                var checkbox = new CheckBox
                {
                    Content = category.Name,
                    Tag = category.Id,
                    IsChecked = selectedCategoryIds.Contains(category.Id)
                };

                CheckboxesPanel.Children.Add(checkbox);
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedCategoryIds.Clear();

            foreach (CheckBox checkbox in CheckboxesPanel.Children)
            {
                if (checkbox.IsChecked == true)
                {
                    SelectedCategoryIds.Add((int)checkbox.Tag);
                }
            }

            this.DialogResult = true;
            this.Close();
        }
    }
}