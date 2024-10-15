using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;

namespace GuildWars2Achievements
{
    public partial class MainWindow : Window
    {
        private const string GroupCacheFilePath = "group_cache.json";
        private const string CategoryCacheFilePath = "category_cache.json";
        private const string AchievementCacheFilePath = "achievement_cache.json";
        private const string UserProgressFilePath = "user_progress.json";
        private const string SelectedSeasonalDailiesFilePath = "selected_seasonal_dailies.json";
        private const string ApiBaseUrl = "https://api.guildwars2.com/v2";
        private const string ApiKeyFilePath = "api_key.txt";
        private static readonly HttpClient httpClient = new HttpClient();
        private Dictionary<string, AchievementGroup> groupCache = new Dictionary<string, AchievementGroup>();
        private Dictionary<int, AchievementCategory> categoryCache = new Dictionary<int, AchievementCategory>();
        private Dictionary<int, Achievement> achievementCache = new Dictionary<int, Achievement>();
        private AchievementCategory? currentCategory;
        private List<Achievement> currentAchievements = new List<Achievement>();
        private List<AchievementCategory> seasonalDailyCategories = new List<AchievementCategory>();
        private List<int> selectedSeasonalDailyCategoryIds = new List<int>();
        private ItemsControl itemsControl;
        private DispatcherTimer countdownTimer;
        private bool isDataLoaded = false;
        private HashSet<int> visibleDailyAchievementIds = new HashSet<int>();
        private bool hideCompleted = false;
        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private void UpdateOverallProgress()
        {
            int totalAchievements = achievementCache.Values.Count(a => !a.IsDaily);
            int completedAchievements = achievementCache.Values.Count(a => !a.IsDaily && a.IsComplete);
            double progressPercentage = (double)completedAchievements / totalAchievements * 100;

            OverallProgressBar.Value = progressPercentage;
            OverallProgressText.Text = $"Overall Progress: {progressPercentage:F2}%";
        }

        private void UpdateDailyProgress()
        {
            int totalDailyAchievements = visibleDailyAchievementIds.Count;
            int completedDailyAchievements = visibleDailyAchievementIds.Count(id => achievementCache.ContainsKey(id) && achievementCache[id].IsComplete);

            if (totalDailyAchievements == 0)
            {
                DailyProgressBar.Value = 0;
                DailyProgressText.Text = "Daily Progress: 0%";
                return;
            }

            double progressPercentage = (double)completedDailyAchievements / totalDailyAchievements * 100;
            DailyProgressBar.Value = progressPercentage;
            DailyProgressText.Text = $"Daily Progress: {progressPercentage:F2}%";
        }

        private void SelectSeasonalDailies_Click(object sender, RoutedEventArgs e)
        {
            var selectionWindow = new SelectSeasonalDailiesWindow(seasonalDailyCategories, selectedSeasonalDailyCategoryIds);
            selectionWindow.Owner = this;
            selectionWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            if (selectionWindow.ShowDialog() == true)
            {
                selectedSeasonalDailyCategoryIds = selectionWindow.SelectedCategoryIds;
                UpdateGroupTreeView();
                var json = JsonSerializer.Serialize(selectedSeasonalDailyCategoryIds);
                File.WriteAllText(SelectedSeasonalDailiesFilePath, json);
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            countdownTimer = new DispatcherTimer();
            countdownTimer.Interval = TimeSpan.FromSeconds(1);
            countdownTimer.Tick += CountdownTimer_Tick;
            countdownTimer.Start();

            try
            {
                await LoadAchievementGroups();

                if (File.Exists(SelectedSeasonalDailiesFilePath))
                {
                    var json = File.ReadAllText(SelectedSeasonalDailiesFilePath);
                    selectedSeasonalDailyCategoryIds = JsonSerializer.Deserialize<List<int>>(json);
                }
                else
                {
                    selectedSeasonalDailyCategoryIds = new List<int>();
                }

                UpdateGroupTreeView();

                if (File.Exists(ApiKeyFilePath))
                {
                    string apiKey = File.ReadAllText(ApiKeyFilePath);
                    await FetchPlayerAchievements(apiKey);
                }

                isDataLoaded = true;
                SearchTextBox.TextChanged += SearchTextBox_TextChanged;
                UpdateOverallProgress();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred during initialization: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CountdownTimer_Tick(object sender, EventArgs e)
        {
            DateTime utcNow = DateTime.UtcNow;
            DateTime nextReset = utcNow.Date.AddDays(1);
            TimeSpan timeUntilReset = nextReset - utcNow;

            if (timeUntilReset.TotalSeconds < 0)
            {
                timeUntilReset = TimeSpan.Zero;
            }

            string timeString = timeUntilReset.ToString(@"hh\:mm\:ss");
            Dispatcher.Invoke(() =>
            {
                CountdownLabel.Content = "Daily Reset In: " + timeString;
            });
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PerformSearch();
        }

        private void EnterApiKey_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new InputDialog();
            dialog.Owner = this;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            if (dialog.ShowDialog() == true)
            {
                string apiKey = dialog.ResponseText;
                File.WriteAllText(ApiKeyFilePath, apiKey);
                FetchPlayerAchievements(apiKey);
            }
        }

        private void UpdateGroupTreeView()
        {
            var updatedGroups = new List<AchievementGroup>();
            var newVisibleDailyAchievementIds = new HashSet<int>();

            foreach (var group in groupCache.Values.OrderBy(g => g.Order))
            {
                // **Exclude the "Bonus Events" group entirely**
                if (group.Name.Equals("Bonus Events", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Skip this group
                }

                var groupCopy = new AchievementGroup
                {
                    Id = group.Id,
                    Name = group.Name,
                    Order = group.Order,
                    Categories = group.Categories,
                    CategoriesDetails = new List<AchievementCategory>()
                };

                foreach (var category in group.CategoriesDetails)
                {
                    // **Include the category based on selected seasonal dailies**
                    if (seasonalDailyCategories.Any(c => c.Id == category.Id))
                    {
                        if (selectedSeasonalDailyCategoryIds.Contains(category.Id))
                        {
                            groupCopy.CategoriesDetails.Add(category);

                            if (group.Name.Equals("Daily", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var achievementId in category.Achievements)
                                {
                                    newVisibleDailyAchievementIds.Add(achievementId);
                                }
                            }
                        }
                    }
                    else
                    {
                        groupCopy.CategoriesDetails.Add(category);

                        if (group.Name.Equals("Daily", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var achievementId in category.Achievements)
                            {
                                newVisibleDailyAchievementIds.Add(achievementId);
                            }
                        }
                    }
                }

                if (groupCopy.CategoriesDetails.Any())
                {
                    updatedGroups.Add(groupCopy);
                }
            }

            GroupTreeView.ItemsSource = null;
            GroupTreeView.ItemsSource = updatedGroups;
            visibleDailyAchievementIds = newVisibleDailyAchievementIds;
            UpdateDailyProgress();
        }

        private async Task FetchPlayerAchievements(string apiKey)
        {
            try
            {
                string url = $"{ApiBaseUrl}/account/achievements?access_token={apiKey}";
                var playerAchievements = await FetchFromApi<List<PlayerAchievement>>(url);

                if (playerAchievements != null)
                {
                    if (groupCache.Count == 0)
                    {
                        await LoadAchievementGroups();
                    }

                    foreach (var playerAchievement in playerAchievements)
                    {
                        if (achievementCache.TryGetValue(playerAchievement.Id, out var achievement))
                        {
                            achievement.IsComplete = playerAchievement.Done;
                        }
                    }

                    RefreshAchievementList();
                    UpdateOverallProgress();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error fetching player achievements: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadAchievementGroups()
        {
            if (groupCache.Count > 0)
            {
                UpdateGroupTreeView();
                return;
            }

            try
            {
                if (File.Exists(GroupCacheFilePath) && File.Exists(CategoryCacheFilePath) && File.Exists(AchievementCacheFilePath))
                {
                    var groupCacheJson = File.ReadAllText(GroupCacheFilePath);
                    var categoryCacheJson = File.ReadAllText(CategoryCacheFilePath);
                    var achievementCacheJson = File.ReadAllText(AchievementCacheFilePath);
                    groupCache = JsonSerializer.Deserialize<Dictionary<string, AchievementGroup>>(groupCacheJson);
                    categoryCache = JsonSerializer.Deserialize<Dictionary<int, AchievementCategory>>(categoryCacheJson);
                    achievementCache = JsonSerializer.Deserialize<Dictionary<int, Achievement>>(achievementCacheJson);
                    RebuildSeasonalDailyCategories();

                    if (File.Exists(UserProgressFilePath))
                    {
                        var userProgressJson = File.ReadAllText(UserProgressFilePath);
                        var userProgress = JsonSerializer.Deserialize<Dictionary<int, UserAchievementProgress>>(userProgressJson);

                        foreach (var kvp in userProgress)
                        {
                            if (achievementCache.TryGetValue(kvp.Key, out var achievement))
                            {
                                achievement.IsComplete = kvp.Value.IsComplete;
                                achievement.Progress = kvp.Value.Progress;
                                achievement.CurrentTier = kvp.Value.CurrentTier;
                            }
                        }
                    }

                    foreach (var achievement in achievementCache.Values)
                    {
                        achievement.PropertyChanged += Achievement_PropertyChanged;
                    }

                    return;
                }
                else
                {
                    await FetchDataFromApiAndCache();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading cached data: {ex.Message}");
                await FetchDataFromApiAndCache();
            }
        }

        private void RebuildSeasonalDailyCategories()
        {
            seasonalDailyCategories.Clear();

            foreach (var group in groupCache.Values)
            {
                if (group.Name == "Daily")
                {
                    foreach (var categoryId in group.Categories)
                    {
                        if (categoryCache.TryGetValue(categoryId, out var category))
                        {
                            if (category.Order == 0 || category.Order == 1)
                            {
                                if (!seasonalDailyCategories.Any(c => c.Id == category.Id))
                                {
                                    seasonalDailyCategories.Add(category);
                                }
                            }
                        }
                    }
                }
            }
        }

        private async Task FetchDataFromApiAndCache()
        {
            try
            {
                Dispatcher.Invoke(() => StatusLabel.Content = "Loading data from API...");
                Dispatcher.Invoke(() => IsEnabled = false);
                string groupsUrl = $"{ApiBaseUrl}/achievements/groups";
                var groupIds = await FetchFromApi<List<string>>(groupsUrl);

                if (groupIds != null && groupIds.Count > 0)
                {
                    string groupDetailsUrl = $"{ApiBaseUrl}/achievements/groups?ids={string.Join(",", groupIds)}";
                    var groupDetails = await FetchFromApi<List<AchievementGroup>>(groupDetailsUrl);

                    if (groupDetails != null)
                    {
                        var sortedGroups = groupDetails.OrderBy(g => g.Order).ToList();
                        foreach (var group in sortedGroups)
                        {
                            groupCache[group.Id] = group;
                        }

                        await FetchAndCacheCategories(sortedGroups);
                        SaveCacheToFile(GroupCacheFilePath, groupCache);
                        SaveCacheToFile(CategoryCacheFilePath, categoryCache);
                        SaveCacheToFile(AchievementCacheFilePath, achievementCache);

                        foreach (var achievement in achievementCache.Values)
                        {
                            achievement.PropertyChanged += Achievement_PropertyChanged;
                        }

                        RebuildSeasonalDailyCategories();
                    }
                }

                Dispatcher.Invoke(() => StatusLabel.Content = "Data loaded successfully.");
                Dispatcher.Invoke(() => IsEnabled = true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching data from API: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    StatusLabel.Content = "Error loading data.";
                    IsEnabled = true;
                });
            }
        }

        private void SaveCacheToFile<T>(string filePath, T data)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(filePath, json);
        }

        public class UserAchievementProgress
        {
            public bool IsComplete { get; set; }
            public int Progress { get; set; }
            public int CurrentTier { get; set; }
        }

        private async Task FetchAndCacheCategories(List<AchievementGroup> groups)
        {
            seasonalDailyCategories.Clear();

            foreach (var group in groups)
            {
                var categoryIds = group.Categories;
                if (categoryIds.Count > 0)
                {
                    if (categoryIds.All(id => categoryCache.ContainsKey(id)))
                    {
                        group.CategoriesDetails = categoryIds.Select(id => categoryCache[id]).ToList();
                        continue;
                    }

                    string categoryUrl = $"{ApiBaseUrl}/achievements/categories?ids={string.Join(",", categoryIds)}";
                    var categoryDetails = await FetchFromApi<List<AchievementCategory>>(categoryUrl);

                    if (categoryDetails != null)
                    {
                        foreach (var category in categoryDetails)
                        {
                            categoryCache[category.Id] = category;

                            if (group.Name == "Daily" && (category.Order == 0 || category.Order == 1))
                            {
                                if (!seasonalDailyCategories.Any(c => c.Id == category.Id))
                                {
                                    seasonalDailyCategories.Add(category);
                                }
                            }

                            if (category.Achievements != null && category.Achievements.Count > 0)
                            {
                                var achievementsToFetch = category.Achievements.Where(id => !achievementCache.ContainsKey(id)).ToList();
                                if (achievementsToFetch.Count > 0)
                                {
                                    string achievementUrl = $"{ApiBaseUrl}/achievements?ids={string.Join(",", achievementsToFetch)}";
                                    var achievements = await FetchFromApi<List<Achievement>>(achievementUrl);

                                    if (achievements != null)
                                    {
                                        foreach (var achievement in achievements)
                                        {
                                            if (group.Name == "Daily")
                                            {
                                                achievement.IsDaily = true;
                                            }
                                            else
                                            {
                                                achievement.IsDaily = false;
                                            }

                                            achievementCache[achievement.Id] = achievement;
                                        }
                                    }
                                }
                            }
                        }

                        group.CategoriesDetails = categoryDetails
                            .Where(c => c.Achievements != null && c.Achievements.Count > 0)
                            .OrderBy(c => c.Order)
                            .ToList();
                    }
                }
            }
        }

        private void Achievement_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is Achievement achievement)
            {
                SaveUserProgress(achievement);
            }

            if (e.PropertyName == nameof(Achievement.IsComplete))
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateOverallProgress();
                    UpdateDailyProgress();
                });
            }
        }

        private void SaveUserProgress(Achievement updatedAchievement)
        {
            Dictionary<int, UserAchievementProgress> userProgress;

            if (File.Exists(UserProgressFilePath))
            {
                var userProgressJson = File.ReadAllText(UserProgressFilePath);
                userProgress = JsonSerializer.Deserialize<Dictionary<int, UserAchievementProgress>>(userProgressJson);
            }
            else
            {
                userProgress = new Dictionary<int, UserAchievementProgress>();
            }

            userProgress[updatedAchievement.Id] = new UserAchievementProgress
            {
                IsComplete = updatedAchievement.IsComplete,
                Progress = updatedAchievement.Progress,
                CurrentTier = updatedAchievement.CurrentTier
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(userProgress, options);
            File.WriteAllText(UserProgressFilePath, json);
        }

        private async void RefreshData_Click(object sender, RoutedEventArgs e)
        {
            var confirmDialog = new ConfirmationDialog("Are you sure you want to refresh the data? This may take some time.");
            confirmDialog.Owner = this;
            confirmDialog.ShowDialog();

            if (confirmDialog.Result)
            {
                StatusLabel.Content = "Refreshing data...";
                IsEnabled = false;
                groupCache.Clear();
                categoryCache.Clear();
                achievementCache.Clear();

                if (File.Exists(GroupCacheFilePath)) File.Delete(GroupCacheFilePath);
                if (File.Exists(CategoryCacheFilePath)) File.Delete(CategoryCacheFilePath);
                if (File.Exists(AchievementCacheFilePath)) File.Delete(AchievementCacheFilePath);

                await FetchDataFromApiAndCache();
                UpdateGroupTreeView();
                RefreshAchievementList();

                StatusLabel.Content = "Data refreshed successfully.";
                IsEnabled = true;

                MessageBox.Show(this, "Data refreshed successfully.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            PerformSearch();
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                PerformSearch();
                e.Handled = true;
            }
        }

        private void PerformSearch()
        {
            if (!isDataLoaded)
            {
                return;
            }

            string searchText = SearchTextBox.Text.Trim().ToLower();

            if (string.IsNullOrWhiteSpace(searchText))
            {
                foreach (var category in categoryCache.Values)
                {
                    category.FilteredAchievements = null;
                }

                UpdateGroupTreeView();
                return;
            }

            var filteredGroups = new List<AchievementGroup>();

            foreach (var group in groupCache.Values)
            {
                var matchingCategories = new List<AchievementCategory>();

                foreach (var category in group.CategoriesDetails)
                {
                    var matchingAchievements = category.Achievements
                        .Where(id => achievementCache.ContainsKey(id) &&
                                     achievementCache[id].Name.ToLower().Contains(searchText))
                        .Select(id => achievementCache[id])
                        .ToList();

                    if (category.Name.ToLower().Contains(searchText))
                    {
                        matchingAchievements = category.Achievements.Select(id => achievementCache[id]).ToList();
                    }

                    if (matchingAchievements.Any())
                    {
                        var newCategory = new AchievementCategory
                        {
                            Id = category.Id,
                            Name = category.Name,
                            Icon = category.Icon,
                            Order = category.Order,
                            Achievements = category.Achievements,
                            FilteredAchievements = matchingAchievements
                        };

                        matchingCategories.Add(newCategory);
                    }
                }

                if (matchingCategories.Any())
                {
                    var newGroup = new AchievementGroup
                    {
                        Id = group.Id,
                        Name = group.Name,
                        Order = group.Order,
                        CategoriesDetails = matchingCategories
                    };
                    filteredGroups.Add(newGroup);
                }
            }

            if (!filteredGroups.Any())
            {
                this.Activate();

                MessageBox.Show(this, "No results found.", "Search", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            GroupTreeView.ItemsSource = filteredGroups.OrderBy(g => g.Order).ToList();
        }

        private async void GroupTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is AchievementCategory selectedCategory)
            {
                currentCategory = selectedCategory;

                List<Achievement> achievementsToDisplay = new List<Achievement>();

                if (selectedCategory.FilteredAchievements != null)
                {
                    achievementsToDisplay = selectedCategory.FilteredAchievements;
                }
                else
                {
                    if (selectedCategory.Achievements != null && selectedCategory.Achievements.Count > 0)
                    {
                        if (selectedCategory.Achievements.All(id => achievementCache.ContainsKey(id)))
                        {
                            achievementsToDisplay = selectedCategory.Achievements.Select(id => achievementCache[id]).ToList();
                        }
                        else
                        {
                            string achievementUrl = $"{ApiBaseUrl}/achievements?ids={string.Join(",", selectedCategory.Achievements)}";
                            Console.WriteLine($"Fetching achievements from: {achievementUrl}");
                            var achievements = await FetchFromApi<List<Achievement>>(achievementUrl);

                            if (achievements != null)
                            {
                                foreach (var achievement in achievements)
                                {
                                    if (string.IsNullOrEmpty(achievement.Icon) && currentCategory != null)
                                    {
                                        achievement.Icon = currentCategory.Icon;
                                    }

                                    achievementCache[achievement.Id] = achievement;
                                }

                                achievementsToDisplay = achievements;
                            }
                        }
                    }
                }

                currentAchievements = achievementsToDisplay;

                foreach (var achievement in currentAchievements)
                {
                    if (string.IsNullOrEmpty(achievement.Icon) && !string.IsNullOrEmpty(selectedCategory.Icon))
                    {
                        achievement.Icon = selectedCategory.Icon;
                    }
                }
                ShowAchievementList(currentAchievements);
            }
        }

        private void HideCompletedAchievements(bool hide)
        {
            if (itemsControl != null)
            {
                var sortedAchievements = currentAchievements.OrderBy(a => a.Name).ToList();

                if (hide)
                {
                    itemsControl.ItemsSource = sortedAchievements.Where(a => !a.IsComplete).ToList();
                }
                else
                {
                    itemsControl.ItemsSource = sortedAchievements;
                }
            }
        }

        private void ShowAchievementList(List<Achievement> achievements)
        {
            currentAchievements = achievements;
            var sortedAchievements = currentAchievements.OrderBy(a => a.Name).ToList();
            itemsControl = new ItemsControl
            {
                ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)))
            };

            itemsControl.ItemTemplate = new DataTemplate(typeof(Achievement))
            {
                VisualTree = BuildAchievementItemTemplate()
            };

            if (hideCompleted)
            {
                itemsControl.ItemsSource = sortedAchievements.Where(a => !a.IsComplete).ToList();
            }
            else
            {
                itemsControl.ItemsSource = sortedAchievements;
            }

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = itemsControl
            };

            var markAllCompleteCheckbox = new CheckBox
            {
                Content = "Mark All Complete",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(10, 10, 0, 5)
            };

            markAllCompleteCheckbox.Checked += (s, e) =>
            {
                foreach (var achievement in currentAchievements)
                {
                    achievement.IsComplete = true;
                    achievement.Progress = achievement.Tiers.Count;
                }
                RefreshAchievementList();

                UpdateOverallProgress();
            };

            markAllCompleteCheckbox.Unchecked += (s, e) =>
            {
                foreach (var achievement in currentAchievements)
                {
                    achievement.IsComplete = false;
                    achievement.Progress = 0;
                }
                RefreshAchievementList();

                UpdateOverallProgress();
            };

            var hideCompletedCheckbox = new CheckBox
            {
                Content = "Hide Completed",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(10, 10, 0, 5),
                IsChecked = hideCompleted
            };

            hideCompletedCheckbox.Checked += (s, e) =>
            {
                hideCompleted = true;
                HideCompletedAchievements(true);
            };

            hideCompletedCheckbox.Unchecked += (s, e) =>
            {
                hideCompleted = false;
                HideCompletedAchievements(false);
            };

            var checkboxPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            checkboxPanel.Children.Add(markAllCompleteCheckbox);
            checkboxPanel.Children.Add(hideCompletedCheckbox);
            DockPanel.SetDock(checkboxPanel, Dock.Top);
            var dockPanel = new DockPanel();
            dockPanel.Children.Add(checkboxPanel);
            dockPanel.Children.Add(scrollViewer);
            RightSideContentControl.Content = dockPanel;
        }

        private void RefreshAchievementList()
        {
            var sortedAchievements = currentAchievements.OrderBy(a => a.Name).ToList();

            if (itemsControl != null)
            {
                if (hideCompleted)
                {
                    itemsControl.ItemsSource = sortedAchievements.Where(a => !a.IsComplete).ToList();
                }
                else
                {
                    itemsControl.ItemsSource = sortedAchievements;
                }
            }
        }

        private void UpdateSingleAchievementDisplay(Achievement achievement)
        {
            foreach (var container in itemsControl.Items)
            {
                if (container is FrameworkElement element && element.DataContext == achievement)
                {
                    element.DataContext = null;
                    element.DataContext = achievement;
                    break;
                }
            }
        }

        private FrameworkElementFactory BuildAchievementItemTemplate()
        {
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.BorderBrushProperty, System.Windows.Media.Brushes.Gray);
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(10));
            borderFactory.SetValue(Border.MarginProperty, new Thickness(5));
            borderFactory.SetValue(Border.WidthProperty, 350.0);
            borderFactory.SetValue(Border.HeightProperty, 85.0);
            borderFactory.AddHandler(Border.MouseDownEvent, new MouseButtonEventHandler(Achievement_MouseDown));

            var gridFactory = new FrameworkElementFactory(typeof(Grid));

            var checkboxColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
            checkboxColumn.SetValue(ColumnDefinition.WidthProperty, new GridLength(30));
            gridFactory.AppendChild(checkboxColumn);

            var iconColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
            iconColumn.SetValue(ColumnDefinition.WidthProperty, new GridLength(70));
            gridFactory.AppendChild(iconColumn);

            var titleColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
            titleColumn.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            gridFactory.AppendChild(titleColumn);

            gridFactory.SetValue(Grid.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            gridFactory.SetValue(Grid.VerticalAlignmentProperty, VerticalAlignment.Center);

            var checkboxFactory = new FrameworkElementFactory(typeof(CheckBox));
            checkboxFactory.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding("IsComplete"));
            checkboxFactory.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
            checkboxFactory.SetValue(CheckBox.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkboxFactory.SetValue(Grid.ColumnProperty, 0);

            checkboxFactory.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((s, e) =>
            {
                if (s is CheckBox checkbox && checkbox.DataContext is Achievement achievement)
                {
                    achievement.Progress = achievement.Tiers.Sum(t => t.Count);
                    achievement.CurrentTier = achievement.Tiers.Count;
                    achievement.IsComplete = true;

                    UpdateSingleAchievementDisplay(achievement);

                    if (hideCompleted)
                    {
                        HideCompletedAchievements(true);
                    }

                    UpdateOverallProgress();
                }
            }));

            checkboxFactory.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((s, e) =>
            {
                if (s is CheckBox checkbox && checkbox.DataContext is Achievement achievement)
                {
                    achievement.IsComplete = false;
                    achievement.Progress = 0;
                    achievement.CurrentTier = 0;
                    UpdateSingleAchievementDisplay(achievement);

                    if (hideCompleted)
                    {
                        HideCompletedAchievements(true);
                    }

                    UpdateOverallProgress();
                }
            }));

            gridFactory.AppendChild(checkboxFactory);

            var iconFactory = BuildIcon();
            iconFactory.SetValue(Grid.ColumnProperty, 1);
            gridFactory.AppendChild(iconFactory);

            var stackPanelFactory = new FrameworkElementFactory(typeof(StackPanel));
            stackPanelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
            stackPanelFactory.SetValue(Grid.ColumnProperty, 2);

            var nameFactory = BuildName();
            stackPanelFactory.AppendChild(nameFactory);

            var statusFactory = new FrameworkElementFactory(typeof(TextBlock));
            statusFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            statusFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("ProgressDisplay"));
            statusFactory.SetValue(TextBlock.FontSizeProperty, 12.0);
            statusFactory.SetValue(TextBlock.MarginProperty, new Thickness(5, 5, 0, 0));
            stackPanelFactory.AppendChild(statusFactory);

            gridFactory.AppendChild(stackPanelFactory);
            borderFactory.AppendChild(gridFactory);
            return borderFactory;
        }

        private FrameworkElementFactory BuildIcon()
        {
            var iconFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
            iconFactory.SetBinding(System.Windows.Controls.Image.SourceProperty, new System.Windows.Data.Binding("Icon"));
            iconFactory.SetValue(System.Windows.Controls.Image.WidthProperty, 64.0);
            iconFactory.SetValue(System.Windows.Controls.Image.HeightProperty, 64.0);
            iconFactory.SetValue(System.Windows.Controls.Image.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            return iconFactory;
        }

        private FrameworkElementFactory BuildName()
        {
            var textBlockFactory = new FrameworkElementFactory(typeof(TextBlock));
            textBlockFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
            textBlockFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            textBlockFactory.SetValue(TextBlock.MarginProperty, new Thickness(5, 10, 0, 0));
            textBlockFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            textBlockFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            textBlockFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            textBlockFactory.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            return textBlockFactory;
        }

        private void Achievement_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is Achievement selectedAchievement)
            {
                string currentIcon = selectedAchievement.Icon;
                ShowAchievementDetails(selectedAchievement, currentIcon);
            }
        }

        private DispatcherTimer _debounceTimer;

        private void ScheduleRefresh()
        {
            if (_debounceTimer == null)
            {
                _debounceTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _debounceTimer.Tick += (s, e) =>
                {
                    _debounceTimer.Stop();
                    RefreshAchievementList();
                };
            }

            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void RemoveApiKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                this.Activate();
                var confirmDialog = new ConfirmationDialog("Are you sure you want to remove the API key?");
                confirmDialog.Owner = this;
                confirmDialog.ShowDialog();

                if (confirmDialog.Result)
                {
                    if (File.Exists(ApiKeyFilePath))
                    {
                        File.Delete(ApiKeyFilePath);
                    }

                    this.Activate();

                    var resetDialog = new ConfirmationDialog("Do you want to reset all achievements to incomplete?");
                    resetDialog.Owner = this;
                    resetDialog.ShowDialog();

                    if (resetDialog.Result)
                    {
                        ResetAchievements();
                        this.Activate();
                        MessageBox.Show(this, "API key removed and achievements reset.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        this.Activate();
                        MessageBox.Show(this, "API key removed.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                this.Activate();

                MessageBox.Show(this, $"An error occurred while removing the API key: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetAchievements()
        {
            foreach (var achievement in achievementCache.Values)
            {
                achievement.IsComplete = false;
                achievement.Progress = 0;
                achievement.CurrentTier = 0;
            }

            RefreshAchievementList();
            UpdateOverallProgress();
            UpdateDailyProgress();
        }

        private void ShowAchievementDetails(Achievement achievement, string icon)
        {
            try
            {
                var grid = new Grid { Margin = new Thickness(10) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var headerStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(10),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };

                var backButton = new Button
                {
                    Content = "<",
                    Width = 24,
                    Height = 24,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -20, 0, 0)
                };
                backButton.Click += (s, e) => ShowAchievementList(currentAchievements);
                headerStack.Children.Add(backButton);

                string resolvedIcon = icon;
                if (string.IsNullOrEmpty(icon) && !string.IsNullOrEmpty(currentCategory?.Icon))
                {
                    resolvedIcon = currentCategory.Icon;
                }

                var headerIconImage = new System.Windows.Controls.Image
                {
                    Width = 32,
                    Height = 32,
                    Margin = new Thickness(10, -20, 0, 0)
                };

                if (!string.IsNullOrEmpty(resolvedIcon))
                {
                    try
                    {
                        headerIconImage.Source = new BitmapImage(new Uri(resolvedIcon, UriKind.RelativeOrAbsolute));
                    }
                    catch (UriFormatException)
                    {
                        MessageBox.Show($"Invalid icon URI: {resolvedIcon}", "Invalid URI", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                }

                headerStack.Children.Add(headerIconImage);

                var title = new TextBlock
                {
                    Text = currentCategory?.Name ?? "Adventure Guide: Volume One",
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(5, -20, 0, 0)
                };
                headerStack.Children.Add(title);

                grid.Children.Add(headerStack);
                Grid.SetRow(headerStack, 0);
                Grid.SetColumnSpan(headerStack, 2);
                var leftStackPanel = new StackPanel { Margin = new Thickness(0, 50, 20, 0) };
                var iconImage = new System.Windows.Controls.Image { Width = 96, Height = 96, Margin = new Thickness(0, -50, -5, 0) };

                if (!string.IsNullOrEmpty(resolvedIcon))
                {
                    try
                    {
                        iconImage.Source = new BitmapImage(new Uri(resolvedIcon, UriKind.RelativeOrAbsolute));
                    }
                    catch (UriFormatException)
                    {
                        MessageBox.Show($"Invalid icon URI: {resolvedIcon}", "Invalid URI", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                leftStackPanel.Children.Add(iconImage);
                grid.Children.Add(leftStackPanel);
                Grid.SetRow(leftStackPanel, 1);
                Grid.SetColumn(leftStackPanel, 0);
                var rightStackPanel = new StackPanel
                {
                    Margin = new Thickness(0, 0, 50, 0),
                    HorizontalAlignment = HorizontalAlignment.Left
                };

                var achievementTitle = new TextBlock
                {
                    Text = achievement.Name,
                    FontSize = 30,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Left,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 5),
                    MaxWidth = 600
                };
                rightStackPanel.Children.Add(achievementTitle);
                var requirementText = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 18,
                    Margin = new Thickness(0, 5, 0, 10)
                };

                if (achievement.Requirement.Contains("<c=@reminder>"))
                {
                    var requirementParts = achievement.Requirement.Split(new[] { "<c=@reminder>", "</c>" }, StringSplitOptions.None);
                    if (requirementParts.Length > 0)
                    {
                        requirementText.Text = requirementParts[0].Trim();
                        rightStackPanel.Children.Add(requirementText);

                        if (requirementParts.Length > 1)
                        {
                            var reminderText = new TextBlock
                            {
                                Text = requirementParts[1].Trim(),
                                FontStyle = FontStyles.Italic,
                                FontSize = 14,
                                Margin = new Thickness(0, 0, 0, 10),
                                TextWrapping = TextWrapping.Wrap
                            };
                            rightStackPanel.Children.Add(reminderText);
                        }
                    }
                }
                else
                {
                    requirementText.Text = achievement.Requirement;
                    rightStackPanel.Children.Add(requirementText);
                }
                var tierSection = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                rightStackPanel.Children.Add(tierSection);
                int savedCurrentTier = achievement.CurrentTier;
                int savedProgress = achievement.Progress;
                UpdateTierSection(achievement, tierSection, savedCurrentTier);

                var buttonPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 0)
                };

                var resetButton = new Button
                {
                    Content = "Reset",
                    Width = 50,
                    Height = 30,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, -20, 10, 0)
                };

                var wikiButton = new Button
                {
                    Content = "Wiki",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, -20, 0, 0),
                    Height = 30,
                    Width = 50
                };
                wikiButton.Click += (s, e) =>
                {
                    string wikiUrl = $"https://wiki.guildwars2.com/wiki/{Uri.EscapeDataString(achievement.Name)}";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = wikiUrl,
                        UseShellExecute = true
                    });
                };

                buttonPanel.Children.Add(resetButton);
                buttonPanel.Children.Add(wikiButton);
                rightStackPanel.Children.Add(buttonPanel);
                grid.Children.Add(rightStackPanel);
                Grid.SetRow(rightStackPanel, 1);
                Grid.SetColumn(rightStackPanel, 1);
                RightSideContentControl.Content = grid;
                resetButton.Click += (s, e) =>
                {
                    achievement.Progress = 0;
                    achievement.CurrentTier = 0;
                    achievement.IsComplete = false;
                    UpdateTierSection(achievement, rightStackPanel, 0);
                    ShowAchievementDetails(achievement, icon);
                    UpdateOverallProgress();
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error displaying achievement details: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateTierSection(Achievement achievement, StackPanel rightStackPanel, int currentTier)
        {
            int progress = achievement.Progress;
            rightStackPanel.Children.Clear();
            if (achievement.IsComplete || currentTier >= achievement.Tiers.Count)
            {
                var completedText = new TextBlock
                {
                    Text = "Completed!",
                    FontSize = 24,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.Green),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 45)
                };
                rightStackPanel.Children.Add(completedText);
                return;
            }

            var tier = achievement.Tiers[currentTier];
            var parentPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            };

            var tierLabel = new TextBlock
            {
                Text = $"Tier {currentTier + 1}: Objectives",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.DarkRed),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5)
            };

            parentPanel.Children.Add(tierLabel);

            var objectiveControlsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0)
            };

            var decrementButton = new Button
            {
                Content = "<",
                Width = 12,
                Height = 12,
                Padding = new Thickness(0),
                FontSize = 7.5,
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(15, 0, 0, 0)
            };

            var incrementButton = new Button
            {
                Content = ">",
                Width = 12,
                Height = 12,
                Padding = new Thickness(0),
                FontSize = 7.5,
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0)
            };

            var tierObjectivesCountRun = new TextBlock
            {
                Text = $"{progress}/{tier.Count}",
                FontSize = 25,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.Red),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0)
            };

            objectiveControlsPanel.Children.Add(decrementButton);
            objectiveControlsPanel.Children.Add(tierObjectivesCountRun);
            objectiveControlsPanel.Children.Add(incrementButton);
            parentPanel.Children.Add(objectiveControlsPanel);
            rightStackPanel.Children.Add(parentPanel);
            var incrementTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var decrementTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            bool isIncrementHeld = false;
            bool isDecrementHeld = false;
            incrementButton.PreviewMouseDown += (s, e) =>
            {
                isIncrementHeld = true;
                incrementTimer.Start();
                incrementTimer.Tick += (sender, args) =>
                {
                    if (isIncrementHeld && progress < tier.Count)
                    {
                        progress++;
                        achievement.Progress = progress;
                        tierObjectivesCountRun.Text = $"{progress}/{tier.Count}";

                        if (progress == tier.Count)
                        {
                            incrementTimer.Stop();
                            achievement.CurrentTier = currentTier + 1;
                            if (achievement.CurrentTier >= achievement.Tiers.Count)
                            {
                                achievement.IsComplete = true;
                                UpdateOverallProgress();
                            }

                            UpdateTierSection(achievement, rightStackPanel, achievement.CurrentTier);
                        }
                    }
                };
            };

            incrementButton.PreviewMouseUp += (s, e) =>
            {
                isIncrementHeld = false;
                incrementTimer.Stop();
            };

            decrementButton.PreviewMouseDown += (s, e) =>
            {
                isDecrementHeld = true;
                decrementTimer.Start();
                decrementTimer.Tick += (sender, args) =>
                {
                    if (isDecrementHeld && progress > 0)
                    {
                        progress--;
                        achievement.Progress = progress;
                        tierObjectivesCountRun.Text = $"{progress}/{tier.Count}";

                        if (achievement.IsComplete)
                        {
                            achievement.IsComplete = false;
                            UpdateOverallProgress();
                        }
                    }
                };
            };

            decrementButton.PreviewMouseUp += (s, e) =>
            {
                isDecrementHeld = false;
                decrementTimer.Stop();
            };

            incrementButton.Click += (s, e) =>
            {
                if (!isIncrementHeld && progress < tier.Count)
                {
                    progress++;
                    achievement.Progress = progress;
                    tierObjectivesCountRun.Text = $"{progress}/{tier.Count}";

                    if (progress == tier.Count)
                    {
                        achievement.CurrentTier = currentTier + 1;

                        if (achievement.CurrentTier >= achievement.Tiers.Count)
                        {
                            achievement.IsComplete = true;
                            UpdateOverallProgress();
                        }

                        UpdateTierSection(achievement, rightStackPanel, achievement.CurrentTier);
                    }
                }
            };

            decrementButton.Click += (s, e) =>
            {
                if (!isDecrementHeld && progress > 0)
                {
                    progress--;
                    achievement.Progress = progress;
                    tierObjectivesCountRun.Text = $"{progress}/{tier.Count}";

                    if (achievement.IsComplete)
                    {
                        achievement.IsComplete = false;
                        UpdateOverallProgress();
                    }
                }
            };
        }

        private async Task<T?> FetchFromApi<T>(string url)
        {
            try
            {
                var response = await httpClient.GetStringAsync(url);
                return JsonSerializer.Deserialize<T>(response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"API Error: {ex.Message}");
                return default;
            }
        }
    }

    public class AchievementGroup
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("order")]
        public int Order { get; set; }

        [JsonPropertyName("categories")]
        public List<int> Categories { get; set; } = new List<int>();

        public List<AchievementCategory> CategoriesDetails { get; set; } = new List<AchievementCategory>();
    }

    public class AchievementCategory
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = string.Empty;

        [JsonPropertyName("order")]
        public int Order { get; set; }

        [JsonPropertyName("achievements")]
        public List<int> Achievements { get; set; } = new List<int>();

        public List<Achievement> FilteredAchievements { get; set; } = null;
    }

    public class Achievement : INotifyPropertyChanged
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("requirement")]
        public string Requirement { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = string.Empty;

        [JsonPropertyName("tiers")]
        public List<Tier> Tiers { get; set; } = new List<Tier>();

        private int _progress;
        private int _currentTier;
        private bool _isComplete;
        public bool IsDaily { get; set; }
        public int Progress
        {
            get => _progress;
            set
            {
                if (_progress != value)
                {
                    _progress = value;
                    OnPropertyChanged(nameof(Progress));
                    OnPropertyChanged(nameof(ProgressDisplay));
                }
            }
        }

        public int CurrentTier
        {
            get => _currentTier;
            set
            {
                if (_currentTier != value)
                {
                    _currentTier = value;
                    OnPropertyChanged(nameof(CurrentTier));
                    OnPropertyChanged(nameof(ProgressDisplay));
                }
            }
        }

        private void CheckCompletionStatus()
        {
            if (_currentTier >= Tiers.Count)
            {
                IsComplete = true;
            }
        }

        public bool IsComplete
        {
            get => _isComplete;
            set
            {
                if (_isComplete != value)
                {
                    _isComplete = value;
                    OnPropertyChanged(nameof(IsComplete));
                    OnPropertyChanged(nameof(ProgressDisplay));
                }
            }
        }

        public string ProgressDisplay
        {
            get
            {
                if (IsComplete || CurrentTier >= Tiers.Count)
                {
                    return "Completed";
                }

                var currentTier = Tiers[CurrentTier];
                return $"Tier {CurrentTier + 1}: Objectives {Progress}/{currentTier.Count}";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class PlayerAchievement
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    public class Tier
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }
    }
}