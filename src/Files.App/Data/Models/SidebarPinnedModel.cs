// Copyright (c) 2023 Files Community
// Licensed under the MIT License. See the LICENSE.

using Files.App.Data.Items;
using Files.App.UserControls.Widgets;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json.Serialization;
using Windows.Storage.FileProperties;
using static Files.App.Constants;

namespace Files.App.Data.Models
{
	public class SidebarPinnedModel
	{
		private IUserSettingsService userSettingsService { get; } = Ioc.Default.GetRequiredService<IUserSettingsService>();
		private IQuickAccessService QuickAccessService { get; } = Ioc.Default.GetRequiredService<IQuickAccessService>();

		public EventHandler<NotifyCollectionChangedEventArgs>? DataChanged;

		private readonly SemaphoreSlim addSyncSemaphore = new(1, 1);

		public List<string> FavoriteItems { get; set; } = new List<string>();

		public readonly List<INavigationControlItem> favoriteList = new();

		[JsonIgnore]
		public IReadOnlyList<INavigationControlItem> Favorites
		{
			get
			{
				lock (favoriteList)
					return favoriteList.ToList().AsReadOnly();
			}
		}

		/// <summary>
		/// Updates items with the pinned items from the explorer sidebar
		/// </summary>
		public async Task UpdateItemsWithExplorer()
		{
			await addSyncSemaphore.WaitAsync();

			try
			{
				FavoriteItems = (await QuickAccessService.GetPinnedFoldersAsync())
					.Where(link => (bool?)link.Properties["System.Home.IsPinned"] ?? false)
					.Select(link => link.FilePath).ToList();
				RemoveStaleSidebarItems();
				await AddAllItemsToSidebar();
			}
			finally
			{
				addSyncSemaphore.Release();
			}
		}

		/// <summary>
		/// Returns the index of the location item in the navigation sidebar
		/// </summary>
		/// <param name="locationItem">The location item</param>
		/// <returns>Index of the item</returns>
		public int IndexOfItem(INavigationControlItem locationItem)
		{
			lock (favoriteList)
			{
				return favoriteList.FindIndex(x => x.Path == locationItem.Path);
			}
		}

		public async Task<LocationItem> CreateLocationItemFromPathAsync(string path)
		{
			var item = await FilesystemTasks.Wrap(() => DriveHelpers.GetRootFromPathAsync(path));
			var res = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderFromPathAsync(path, item));
			LocationItem locationItem;

			if (string.Equals(path, UserEnvironmentPaths.RecycleBinPath, StringComparison.OrdinalIgnoreCase))
				locationItem = LocationItem.Create<RecycleBinLocationItem>();
			else
			{
				locationItem = LocationItem.Create<LocationItem>();

				if (path.Equals(UserEnvironmentPaths.MyComputerPath, StringComparison.OrdinalIgnoreCase))
					locationItem.Text = "ThisPC".GetLocalizedResource();
				else if (path.Equals(UserEnvironmentPaths.NetworkFolderPath, StringComparison.OrdinalIgnoreCase))
					locationItem.Text = "Network".GetLocalizedResource();
			}

			locationItem.Path = path;
			locationItem.Section = SectionType.Favorites;
			locationItem.MenuOptions = new ContextMenuOptions
			{
				IsLocationItem = true,
				ShowProperties = true,
				ShowUnpinItem = true,
				ShowShellItems = true,
				ShowEmptyRecycleBin = string.Equals(path, UserEnvironmentPaths.RecycleBinPath, StringComparison.OrdinalIgnoreCase)
			};
			locationItem.IsDefaultLocation = false;
			locationItem.Text = res.Result?.DisplayName ?? Path.GetFileName(path.TrimEnd('\\'));

			locationItem.IsInvalid = !(res || (FilesystemResult)FolderHelpers.CheckFolderAccessWithWin32(path));
			if (!locationItem.IsInvalid)
			{
				if (locationItem.Path == UserEnvironmentPaths.RecycleBinPath)
				{
					int recycleBinIconIndex = UIHelpers.GetAdaptedRecycleBinIconIndex();
					locationItem.IconData = UIHelpers.GetSidebarIconResourceInfo(recycleBinIconIndex).IconData;
					await UpdateLocationItemIcon(locationItem);
				}
				else if (res)
				{
					locationItem.IconData = await FileThumbnailHelper.LoadIconFromStorageItemAsync(res.Result, 16u, ThumbnailMode.ListView, ThumbnailOptions.UseCurrentScale);
					await UpdateLocationItemIcon(locationItem);
				}

				if (locationItem.IconData is null)
				{
					locationItem.IconData = await FileThumbnailHelper.LoadIconWithoutOverlayAsync(path, 48u);
					await UpdateLocationItemIcon(locationItem);
				}
			}
			else
			{
				locationItem.Icon = await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => UIHelpers.GetSidebarIconResource(Constants.ImageRes.Folder));
				Debug.WriteLine($"Pinned item was invalid {res.ErrorCode}, item: {path}");
			}

			return locationItem;

			async Task UpdateLocationItemIcon(LocationItem locationItem)
			{
				if (locationItem.IconData is not null)
				{
					locationItem.Icon = await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => locationItem.IconData.ToBitmapAsync());
				}
			}
		}

		/// <summary>
		/// Adds the item (from a path) to the navigation sidebar
		/// </summary>
		/// <param name="path">The path which to save</param>
		/// <returns>Task</returns>
		public async Task AddItemToSidebarAsync(string path)
		{
			var locationItem = await CreateLocationItemFromPathAsync(path);

			AddLocationItemToSidebar(locationItem);
		}

		/// <summary>
		/// Adds the location item to the navigation sidebar
		/// </summary>
		/// <param name="locationItem">The location item which to save</param>
		private void AddLocationItemToSidebar(LocationItem locationItem)
		{
			int insertIndex = -1;
			lock (favoriteList)
			{
				if (favoriteList.Any(x => x.Path == locationItem.Path))
					return;

				var lastItem = favoriteList.LastOrDefault(x => x.ItemType is NavigationControlItemType.Location);
				insertIndex = lastItem is not null ? favoriteList.IndexOf(lastItem) + 1 : 0;
				favoriteList.Insert(insertIndex, locationItem);
			}

			DataChanged?.Invoke(SectionType.Favorites, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, locationItem, insertIndex));
		}

		/// <summary>
		/// Adds all items to the navigation sidebar
		/// </summary>
		public async Task AddAllItemsToSidebar()
		{
			if (userSettingsService.GeneralSettingsService.ShowFavoritesSection)
				foreach (string path in FavoriteItems)
					await AddItemToSidebarAsync(path);
		}

		/// <summary>
		/// Removes stale items in the navigation sidebar
		/// </summary>
		public void RemoveStaleSidebarItems()
		{
			// Remove unpinned items from favoriteList
			foreach (var childItem in Favorites)
			{
				if (childItem is LocationItem item && !item.IsDefaultLocation && !FavoriteItems.Contains(item.Path))
				{
					lock (favoriteList)
					{
						favoriteList.Remove(item);
					}
					DataChanged?.Invoke(SectionType.Favorites, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item));
				}
			}

			// Remove unpinned items from sidebar
			DataChanged?.Invoke(SectionType.Favorites, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		}

		public async void LoadAsync(object? sender, FileSystemEventArgs e)
		{
			App.QuickAccessManager.PinnedItemsWatcher.EnableRaisingEvents = false;
			await LoadAsync();
			App.QuickAccessManager.UpdateQuickAccessWidget?.Invoke(null, new ModifyQuickAccessEventArgs((await QuickAccessService.GetPinnedFoldersAsync()).ToArray(), true)
			{
				Reset = true
			});
			App.QuickAccessManager.PinnedItemsWatcher.EnableRaisingEvents = true;
		}

		public async Task LoadAsync()
		{
			await UpdateItemsWithExplorer();
		}

		public async void UpdateRecycleBinIcon()
		{
			LocationItem? recycleBinLocationItem = RetrieveRecycleBinLocationElement();

			if (recycleBinLocationItem == null)
				return;

			int recycleBinIconIndex = UIHelpers.GetAdaptedRecycleBinIconIndex();
			recycleBinLocationItem.IconData = UIHelpers.GetSidebarIconResourceInfo(recycleBinIconIndex).IconData;
			if (recycleBinLocationItem.IconData != null)
			{
				recycleBinLocationItem.Icon = await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => recycleBinLocationItem.IconData.ToBitmapAsync());
			}
		}

		private LocationItem? RetrieveRecycleBinLocationElement()
		{
			return favoriteList.Cast<LocationItem>()?.FirstOrDefault(element => element.Path == UserEnvironmentPaths.RecycleBinPath);
		}
	}
}