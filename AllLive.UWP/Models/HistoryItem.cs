using AllLive.Core.Models;
using AllLive.UWP.ViewModels;
using System;

namespace AllLive.UWP.Models
{
    public class HistoryItem : BaseNotifyPropertyChanged
    {
        public int ID { get; set; }
        public string RoomID { get; set; }
        public string UserName { get; set; }
        public string Photo { get; set; }
        public string SiteName { get; set; }
        public DateTime WatchTime { get; set; }

        private LiveStatusType _LiveStatus = LiveStatusType.Offline;
        public LiveStatusType LiveStatus
        {
            get { return _LiveStatus; }
            set
            {
                _LiveStatus = value;
                DoPropertyChanged("LiveStatus");
                DoPropertyChanged("IsLive");
                DoPropertyChanged("IsReplay");
                DoPropertyChanged("IsLiveOrReplay");
            }
        }

        public bool IsLiveOrReplay => LiveStatus == LiveStatusType.Live || LiveStatus == LiveStatusType.Replay;
        public bool IsLive => LiveStatus == LiveStatusType.Live;
        public bool IsReplay => LiveStatus == LiveStatusType.Replay;
    }
}
