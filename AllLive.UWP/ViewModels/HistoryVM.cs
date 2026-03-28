using AllLive.UWP.Helper;
using AllLive.UWP.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Windows.Storage.Pickers;
using Windows.Storage;
using Newtonsoft.Json;

namespace AllLive.UWP.ViewModels
{
    public class HistoryVM : BaseViewModel
    {
        public HistoryVM()
        {
            Items = new ObservableCollection<HistoryItem>();
            CleanCommand = new RelayCommand(Clean);
            InputCommand = new RelayCommand(Input);
            OutputCommand = new RelayCommand(Output);
        }
        public ICommand CleanCommand { get; set; }
        public ICommand InputCommand { get; set; }
        public ICommand OutputCommand { get; set; }

        private ObservableCollection<HistoryItem> _items;
        public ObservableCollection<HistoryItem> Items
        {
            get { return _items; }
            set { _items = value; DoPropertyChanged("Items"); }
        }

        private bool _loadingLiveStatus;
        public bool LoaddingLiveStatus
        {
            get { return _loadingLiveStatus; }
            set { _loadingLiveStatus = value; DoPropertyChanged("LoaddingLiveStatus"); }
        }

        int loadedCount = 0;

        public async void LoadData()
        {
            try
            {
                Loading = true;
                foreach (var item in await DatabaseHelper.GetHistory())
                {
                    Items.Add(item);
                }
                IsEmpty = Items.Count == 0;
                if (!IsEmpty)
                {
                    LoadLiveStatus();
                }
            }
            catch (Exception ex)
            {
                HandleError(ex);
            }
            finally
            {
                Loading = false;
            }
        }

        public override void Refresh()
        {
            base.Refresh();
            Items.Clear();
            LoadData();
        }

        public void LoadLiveStatus()
        {
            LoaddingLiveStatus = true;
            System.Threading.Interlocked.Exchange(ref loadedCount, 0);
            foreach (var item in Items)
            {
                LoadLiveStatus(item);
            }
        }

        public async void LoadLiveStatus(HistoryItem item)
        {
            try
            {
                var site = MainVM.Sites.FirstOrDefault(x => x.Name == item.SiteName);
                if (site != null)
                {
                    var status = await site.LiveSite.GetLiveStatus(item.RoomID);
                    item.LiveStatus = status;
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log($"获取直播状态失败:{item.SiteName}-{item.RoomID}", LogType.ERROR, ex);
            }
            finally
            {
                var currentCount = System.Threading.Interlocked.Increment(ref loadedCount);
                if (currentCount == Items.Count)
                {
                    await Windows.ApplicationModel.Core.CoreApplication.MainView.Dispatcher.RunAsync(
                        Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        LoaddingLiveStatus = false;
                        Items = new ObservableCollection<HistoryItem>(
                            Items.OrderByDescending(x => (int)x.LiveStatus)
                                 .ThenByDescending(x => x.WatchTime));
                    });
                }
            }
        }

        public void RemoveItem(HistoryItem item)
        {
            try
            {
                DatabaseHelper.DeleteHistory(item.ID);
                Items.Remove(item);
                IsEmpty = Items.Count == 0;
            }
            catch (Exception ex)
            {
                HandleError(ex);
            }
        }

        public async void Clean()
        {
            try
            {
                var result = await Utils.ShowDialog("清空记录", $"确定要清除全部观看记录吗?");
                if (!result)
                {
                    return;
                }

                DatabaseHelper.DeleteHistory();
                Items.Clear();
                IsEmpty = true;
            }
            catch (Exception ex)
            {
                HandleError(ex);
            }
        }

        public async void Input()
        {
            FileOpenPicker picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.ViewMode = PickerViewMode.List;
            picker.CommitButtonText = "导入";

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                try
                {
                    var json = await FileIO.ReadTextAsync(file);
                    var items = JsonConvert.DeserializeObject<List<HistoryJsonItem>>(json);
                    foreach (var item in items)
                    {
                        DatabaseHelper.AddHistory(new HistoryItem()
                        {
                            SiteName = item.SiteName,
                            RoomID = item.RoomId,
                            UserName = item.UserName,
                            Photo = item.Face,
                            WatchTime = DateTime.TryParse(item.UpdateTime, out var dt) ? dt : DateTime.Now
                        });
                    }
                    Utils.ShowMessageToast("导入成功");
                    Refresh();
                }
                catch (Exception ex)
                {
                    HandleError(ex);
                    Utils.ShowMessageToast("导入失败");
                }
            }
        }

        public async void Output()
        {
            FileSavePicker picker = new FileSavePicker();
            picker.FileTypeChoices.Add("Json", new List<string>() { ".json" });
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.SuggestedFileName = "history.json";

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                try
                {
                    var items = new List<HistoryJsonItem>();
                    foreach (var item in Items)
                    {
                        var siteId = "";
                        switch (item.SiteName)
                        {
                            case "哔哩哔哩直播":
                                siteId = "bilibili";
                                break;
                            case "斗鱼直播":
                                siteId = "douyu";
                                break;
                            case "虎牙直播":
                                siteId = "huya";
                                break;
                            case "抖音直播":
                                siteId = "douyin";
                                break;
                        }

                        items.Add(new HistoryJsonItem()
                        {
                            SiteId = siteId,
                            Id = $"{siteId}_{item.RoomID}",
                            RoomId = item.RoomID,
                            UserName = item.UserName,
                            Face = item.Photo,
                            UpdateTime = item.WatchTime.ToString("yyyy-MM-dd HH:mm:ss")
                        });
                    }
                    var json = JsonConvert.SerializeObject(items, Formatting.Indented);
                    await FileIO.WriteTextAsync(file, json);
                    Utils.ShowMessageToast("导出成功");
                }
                catch (Exception ex)
                {
                    HandleError(ex);
                    Utils.ShowMessageToast("导出失败");
                }
            }
        }
    }
}
