﻿// Copyright 2017 Google Inc. All Rights Reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using GoogleCloudExtension.Accounts;
using GoogleCloudExtension.DataSources;
using GoogleCloudExtension.SourceBrowsing;
using GoogleCloudExtension.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;

namespace GoogleCloudExtension.StackdriverErrorReporting
{
    /// <summary>
    /// View model to <seealso cref="ErrorReportingDetailToolWindowControl"/> control.
    /// </summary>
    public class ErrorReportingDetailViewModel : ViewModelBase, IDisposable
    {
        private Lazy<IStackdriverErrorReportingDataSource> _datasource;
        private bool _isGroupLoading;
        private bool _isEventLoading;
        private bool _isControlEnabled = true;
        private bool _showError;
        private string _errorString;
        private bool _isAccountChanged;
        private ErrorGroupItem _groupItem;
        private CollectionView _eventItemCollection;
        private TimeRangeItem _selectedTimeRange;
        private readonly Lazy<List<TimeRangeItem>> _timeRangeItemList = new Lazy<List<TimeRangeItem>>(TimeRangeItem.CreateTimeRanges);
        private readonly IGoogleCloudExtensionPackage _package;

        // References to static method dependencies. Mockable for testing.
        internal Action<ErrorGroupItem, StackFrame> ErrorFrameToSourceLine = ShowTooltipUtils.ErrorFrameToSourceLine;
        internal Func<Task<ErrorReportingToolWindow>> ShowErrorReportingToolWindow =
            ToolWindowCommandUtils.ShowToolWindowAsync<ErrorReportingToolWindow>;

        private readonly IStackdriverErrorReportingDataSource _dataSourceOverride = null;

        private IStackdriverErrorReportingDataSource DataSource => _dataSourceOverride ?? _datasource?.Value;

        /// <summary>
        /// Indicate the Google account is set.
        /// </summary>
        public bool IsAccountChanged
        {
            get { return _isAccountChanged; }
            set { SetValueAndRaise(ref _isAccountChanged, value); }
        }

        /// <summary>
        /// The exception message to show as error message.
        /// </summary>
        public string ErrorString
        {
            get { return _errorString; }
            set { SetValueAndRaise(ref _errorString, value); }
        }

        /// <summary>
        /// Show or hide the error message.
        /// </summary>
        public bool ShowError
        {
            get { return _showError; }
            set { SetValueAndRaise(ref _showError, value); }
        }

        /// <summary>
        /// A flag indicating if the entire window is enabled.
        /// </summary>
        public bool IsControlEnabled
        {
            get { return _isControlEnabled; }
            set { SetValueAndRaise(ref _isControlEnabled, value); }
        }

        /// <summary>
        /// A flag indicating if it is loading error group data.
        /// </summary>
        public bool IsGroupLoading
        {
            get { return _isGroupLoading; }
            set { SetValueAndRaise(ref _isGroupLoading, value); }
        }

        /// <summary>
        /// A flag indicating if it is loading error event list.
        /// </summary>
        public bool IsEventLoading
        {
            get { return _isEventLoading; }
            set { SetValueAndRaise(ref _isEventLoading, value); }
        }

        /// <summary>
        /// Gets or sets the error group.
        /// </summary>
        public ErrorGroupItem GroupItem
        {
            get { return _groupItem; }
            private set { SetValueAndRaise(ref _groupItem, value); }
        }

        /// <summary>
        /// The data collection of event item list.
        /// </summary>
        public CollectionView EventItemCollection
        {
            get { return _eventItemCollection; }
            private set { SetValueAndRaise(ref _eventItemCollection, value); }
        }

        /// <summary>
        /// Sets the currently selected time range.
        /// </summary>
        public TimeRangeItem SelectedTimeRangeItem
        {
            get { return _selectedTimeRange; }
            set
            {
                SetValueAndRaise(ref _selectedTimeRange, value);
                if (value != null)
                {
                    ErrorHandlerUtils.HandleExceptionsAsync(UpdateGroupAndEventAsync);
                }
            }
        }

        /// <summary>
        /// Indicates whether the view is visible or not
        /// </summary>
        public bool IsVisibleUnbound { get; set; }

        /// <summary>
        /// Gets the list of time range items.
        /// </summary>
        public IEnumerable<TimeRangeItem> AllTimeRangeItems => _timeRangeItemList.Value;

        /// <summary>
        /// Go back to overview window command.
        /// </summary>
        public ProtectedAsyncCommand OnBackToOverViewCommand { get; }

        /// <summary>
        /// The command that responds to source link button click event.
        /// </summary>
        public ProtectedCommand<StackFrame> OnGotoSourceCommand { get; }

        /// <summary>
        /// Gets the command that responds to auto reload event.
        /// </summary>
        public ProtectedCommand OnAutoReloadCommand { get; }

        internal ErrorReportingDetailViewModel(IStackdriverErrorReportingDataSource dataSourceOverride) : this()
        {
            _dataSourceOverride = dataSourceOverride;
        }

        /// <summary>
        /// Initializes a new instance of <seealso cref="ErrorReportingDetailViewModel"/> class.
        /// </summary>
        public ErrorReportingDetailViewModel()
        {
            _package = GoogleCloudExtensionPackage.Instance;
            IsVisibleUnbound = true;
            OnGotoSourceCommand = new ProtectedCommand<StackFrame>(frame => ErrorFrameToSourceLine(GroupItem, frame));
            OnBackToOverViewCommand = new ProtectedAsyncCommand(async () => await ShowErrorReportingToolWindow());
            OnAutoReloadCommand = new ProtectedCommand(() => ErrorHandlerUtils.HandleExceptionsAsync(UpdateGroupAndEventAsync));
            _datasource = new Lazy<IStackdriverErrorReportingDataSource>(CreateDataSource);

            CredentialsStore.Default.CurrentProjectIdChanged += OnCurrentProjectChanged;
        }

        /// <summary>
        /// Dispose the object, implement IDisposable.
        /// </summary>
        public void Dispose()
        {
            OnAutoReloadCommand.CanExecuteCommand = false;
            CredentialsStore.Default.CurrentProjectIdChanged -= OnCurrentProjectChanged;
        }

        /// <summary>
        /// Hide detail view content when project id is changed.
        /// </summary>
        private void OnCurrentProjectChanged(object sender, EventArgs e)
        {
            IsAccountChanged = true;
            _datasource = new Lazy<IStackdriverErrorReportingDataSource>(CreateDataSource);
        }

        /// <summary>
        /// Update detail view with a new <paramref name="errorGroupItem"/>.
        /// </summary>
        /// <param name="errorGroupItem">The error group item showing in the detail view.</param>
        /// <param name="groupSelectedTimeRangeItem">The selected time range.</param>
        public void UpdateView(ErrorGroupItem errorGroupItem, TimeRangeItem groupSelectedTimeRangeItem)
        {
            if (errorGroupItem == null)
            {
                throw new ErrorReportingException(new ArgumentNullException(nameof(errorGroupItem)));
            }
            if (groupSelectedTimeRangeItem == null)
            {
                throw new ErrorReportingException(new ArgumentNullException(nameof(groupSelectedTimeRangeItem)));
            }

            IsAccountChanged = false;
            GroupItem = errorGroupItem;
            if (groupSelectedTimeRangeItem.GroupTimeRange == SelectedTimeRangeItem?.GroupTimeRange)
            {
                ErrorHandlerUtils.HandleExceptionsAsync(UpdateEventAsync);
            }
            else
            {
                // This will triger a call to UpdateGroupAndEventAsync().
                SelectedTimeRangeItem = AllTimeRangeItems.First(x => x.GroupTimeRange == groupSelectedTimeRangeItem.GroupTimeRange);
            }
        }

        /// <summary>
        /// Load new even group data.
        /// </summary>
        private async Task UpdateEventGroupAsync()
        {
            if (GroupItem == null)
            {
                throw new ErrorReportingException("GroupItem is null.");
            }

            // In most cases, it is because Project id is null
            if (DataSource == null)
            {
                return;
            }

            IsGroupLoading = true;
            ShowError = false;
            try
            {
                var groups = await DataSource?.GetPageOfGroupStatusAsync(
                    SelectedTimeRangeItem.GroupTimeRange,
                    SelectedTimeRangeItem.TimedCountDuration,
                    GroupItem.ErrorGroup.Group.GroupId);
                if (groups?.ErrorGroupStats != null && groups.ErrorGroupStats.Count > 0)
                {
                    GroupItem = new ErrorGroupItem(groups.ErrorGroupStats.FirstOrDefault(), SelectedTimeRangeItem);
                }
                else
                {
                    GroupItem.SetCountEmpty();
                    RaisePropertyChanged(nameof(GroupItem));
                }
            }
            catch (DataSourceException)
            {
                ShowError = true;
                ErrorString = Resources.ErrorReportingDataSourceGenericErrorMessage;
            }
            catch (ErrorReportingException)
            {
                ShowError = true;
                ErrorString = Resources.ErrorReportingInternalCodeErrorGenericMessage;
            }
            finally
            {
                IsGroupLoading = false;
            }
        }

        private async Task UpdateGroupAndEventAsync()
        {
            if (!IsVisibleUnbound || (_package != null && !_package.IsWindowActive()))
            {
                return;
            }

            IsControlEnabled = false;
            try
            {
                await UpdateEventGroupAsync();
                await UpdateEventAsync();
            }
            finally
            {
                IsControlEnabled = true;
            }
        }

        /// <summary>
        /// Update the event list.
        /// </summary>
        private async Task UpdateEventAsync()
        {
            EventItemCollection = null;

            // In most cases, it is because Project id is null
            if (DataSource == null)
            {
                return;
            }

            if (GroupItem.ErrorGroup.TimedCounts != null)
            {
                IsEventLoading = true;
                IsControlEnabled = false;
                ShowError = false;
                try
                {
                    var events = await DataSource?.GetPageOfEventsAsync(
                        GroupItem.ErrorGroup,
                        SelectedTimeRangeItem.EventTimeRange);
                    if (events?.ErrorEvents != null)
                    {
                        EventItemCollection = CollectionViewSource.GetDefaultView(
                            events.ErrorEvents.Where(x => x != null).Select(x => new EventItem(x))) as CollectionView;
                    }
                }
                catch (DataSourceException)
                {
                    ShowError = true;
                    ErrorString = Resources.ErrorReportingDataSourceGenericErrorMessage;
                }
                catch (ErrorReportingException)
                {
                    ShowError = true;
                    ErrorString = Resources.ErrorReportingInternalCodeErrorGenericMessage;
                }
                finally
                {
                    IsEventLoading = false;
                    IsControlEnabled = true;
                }
            }
        }

        private StackdriverErrorReportingDataSource CreateDataSource()
        {
            if (String.IsNullOrWhiteSpace(CredentialsStore.Default.CurrentProjectId))
            {
                return null;
            }
            return new StackdriverErrorReportingDataSource(
                CredentialsStore.Default.CurrentProjectId,
                CredentialsStore.Default.CurrentGoogleCredential,
                GoogleCloudExtensionPackage.Instance.VersionedApplicationName);
        }
    }
}
