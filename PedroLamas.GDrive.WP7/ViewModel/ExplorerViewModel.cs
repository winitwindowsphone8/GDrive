﻿using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Cimbalino.Phone.Toolkit.Extensions;
using Cimbalino.Phone.Toolkit.Services;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Threading;
using PedroLamas.GDrive.Model;
using PedroLamas.GDrive.Service;
using PedroLamas.ServiceModel;
using RestSharp;

namespace PedroLamas.GDrive.ViewModel
{
    public class ExplorerViewModel : ViewModelBase
    {
        private const string GoogleDriveFileFields = "etag,items(description,fileSize,id,labels,mimeType,modifiedDate,title),nextPageToken";

        private readonly IMainModel _mainModel;
        private readonly IGoogleDriveService _googleDriveService;
        private readonly INavigationService _navigationService;
        private readonly IMessageBoxService _messageBoxService;
        private readonly ISystemTrayService _systemTrayService;
        private readonly IPhotoChooserService _photoChooserService;

        private int _pivotSelectedIndex;
        private bool _isSelectionEnabled;
        private RestRequestAsyncHandle _asyncHandle;

        #region Properties

        public string AccountName
        {
            get
            {
                return _mainModel.CurrentAccount.Name.ToUpperInvariant();
            }
        }

        public string CurrentPath
        {
            get
            {
                return _mainModel.CurrentPath;
            }
        }

        public ObservableCollection<GoogleFileViewModel> Files { get; private set; }

        public ObservableCollection<GoogleFileViewModel> PictureFiles { get; private set; }

        public int PivotSelectedIndex
        {
            get
            {
                return _pivotSelectedIndex;
            }
            set
            {
                if (_pivotSelectedIndex == value)
                    return;

                _pivotSelectedIndex = value;

                RaisePropertyChanged(() => PivotSelectedIndex);
            }
        }

        public bool IsSelectionEnabled
        {
            get
            {
                return _isSelectionEnabled;
            }
            set
            {
                if (_isSelectionEnabled == value)
                    return;

                _isSelectionEnabled = value;

                RaisePropertyChanged(() => IsSelectionEnabled);
                RaisePropertyChanged(() => SelectedApplicationBarIndex);
            }
        }

        public int SelectedApplicationBarIndex
        {
            get
            {
                return _isSelectionEnabled ? 1 : 0;
            }
        }

        public RelayCommand<GoogleFileViewModel> OpenFileCommand { get; private set; }

        public RelayCommand<GoogleFileViewModel> ChangeStaredStatusCommand { get; private set; }

        public RelayCommand AddFileCommand { get; private set; }

        public RelayCommand EnableSelectionCommand { get; private set; }

        public RelayCommand RefreshFilesCommand { get; private set; }

        public RelayCommand<IList> DeleteFilesCommand { get; private set; }

        public RelayCommand CreateNewFolderCommand { get; private set; }

        public RelayCommand<GoogleFileViewModel> RenameFileCommand { get; private set; }

        public RelayCommand ShowAboutCommand { get; private set; }

        public RelayCommand PageLoadedCommand { get; private set; }

        public RelayCommand<CancelEventArgs> BackKeyPressCommand { get; private set; }

        public bool IsBusy
        {
            get
            {
                return _systemTrayService.IsBusy;
            }
        }

        #endregion

        public ExplorerViewModel(IMainModel mainModel, IGoogleDriveService googleDriveService, INavigationService navigationService, IMessageBoxService messageBoxService, ISystemTrayService systemTrayService, IPhotoChooserService photoChooserService)
        {
            _mainModel = mainModel;
            _googleDriveService = googleDriveService;
            _navigationService = navigationService;
            _messageBoxService = messageBoxService;
            _systemTrayService = systemTrayService;
            _photoChooserService = photoChooserService;

            Files = new ObservableCollection<GoogleFileViewModel>();
            PictureFiles = new ObservableCollection<GoogleFileViewModel>();

            OpenFileCommand = new RelayCommand<GoogleFileViewModel>(file =>
            {
                if (IsSelectionEnabled)
                {
                    return;
                }

                OpenFile(file);
            });

            ChangeStaredStatusCommand = new RelayCommand<GoogleFileViewModel>(file =>
            {
                if (IsSelectionEnabled)
                {
                    return;
                }

                ChangeStaredStatus(file);
            });

            AddFileCommand = new RelayCommand(UploadFile);

            EnableSelectionCommand = new RelayCommand(() =>
            {
                if (IsBusy)
                {
                    return;
                }

                IsSelectionEnabled = true;
            });

            RefreshFilesCommand = new RelayCommand(RefreshFiles);

            DeleteFilesCommand = new RelayCommand<IList>(files =>
            {
                _messageBoxService.Show("You are about to delete the selected files. Do you wish to proceed?", "Delete files?", new string[] { "delete", "cancel" }, button =>
                {
                    if (button != 0)
                        return;

                    var filesArray = files
                        .Cast<GoogleFileViewModel>()
                        .ToArray();

                    IsSelectionEnabled = false;

                    DeleteNextFile(filesArray
                        .Cast<GoogleFileViewModel>()
                        .GetEnumerator());
                });
            });

            CreateNewFolderCommand = new RelayCommand(CreateNewFolder);

            ShowAboutCommand = new RelayCommand(() =>
            {
                _navigationService.NavigateTo("/View/AboutPage.xaml");
            });

            PageLoadedCommand = new RelayCommand(ExecuteInitialLoad);

            BackKeyPressCommand = new RelayCommand<CancelEventArgs>(e =>
            {
                GoogleDriveFile item;

                if (PivotSelectedIndex == 1)
                {
                    PivotSelectedIndex = 0;

                    e.Cancel = true;
                }
                else if (IsSelectionEnabled)
                {
                    IsSelectionEnabled = false;

                    e.Cancel = true;
                }
                else if (_mainModel.TryPop(out item))
                {
                    AbortCurrentCall();

                    RaisePropertyChanged(() => CurrentPath);

                    RefreshFiles();

                    e.Cancel = true;
                }
                else
                {
                    AbortCurrentCall(true);

                    Files.Clear();
                    PictureFiles.Clear();
                }
            });

            MessengerInstance.Register<RefreshFilesMessage>(this, message =>
            {
                DispatcherHelper.RunAsync(() =>
                {
                    RefreshFiles();
                });
            });
        }

        private void ExecuteInitialLoad()
        {
            if (!_mainModel.ExecuteInitialLoad)
            {
                return;
            }

            _mainModel.ExecuteInitialLoad = false;

            _mainModel.CheckTokenAndExecute<GoogleDriveAbout>((authToken, callback, state) =>
            {
                _systemTrayService.SetProgressIndicator("Reading drive info...");

                _asyncHandle = _googleDriveService.About(_mainModel.CurrentAccount.AuthToken, new GoogleDriveAboutRequest()
                {
                    ETag = _mainModel.CurrentAccount.Info != null ? _mainModel.CurrentAccount.Info.ETag : null
                }, callback, state);
            }, result =>
            {
                switch (result.Status)
                {
                    case ResultStatus.Aborted:
                        break;

                    case ResultStatus.Completed:
                        _mainModel.CurrentAccount.Info = result.Data;
                        _mainModel.Save();

                        RefreshFiles();

                        break;

                    case ResultStatus.Empty:
                        RefreshFiles();

                        break;

                    case ResultStatus.Error:
                        _systemTrayService.HideProgressIndicator();

                        _messageBoxService.Show("Unable to get the drive information!", "Error");

                        break;
                }
            }, null);
        }

        private void OpenFile(GoogleFileViewModel fileViewModel)
        {
            if (fileViewModel.FileModel == null)
                return;

            if (fileViewModel.IsFolder)
            {
                _mainModel.Push(fileViewModel.FileModel);

                RaisePropertyChanged(() => CurrentPath);

                RefreshFiles();
            }
        }

        private void ChangeStaredStatus(GoogleFileViewModel fileViewModel)
        {
            if (fileViewModel.FileModel == null || IsBusy)
                return;

            _mainModel.CheckTokenAndExecute<GoogleDriveFile>((authToken, callback, state) =>
            {
                AbortCurrentCall();

                _systemTrayService.SetProgressIndicator("Changing file stared status...");

                var currentStaredtatus = fileViewModel.FileModel.Labels.Starred;

                _asyncHandle = _googleDriveService.FilesUpdate(authToken, fileViewModel.Id, new GoogleDriveFilesUpdateRequest()
                {
                    File = new GoogleDriveFile()
                    {
                        Labels = new GoogleDriveLabels()
                        {
                            Starred = currentStaredtatus.HasValue ? !currentStaredtatus.Value : true
                        }
                    },
                    Fields = GoogleDriveFileFields
                }, callback, state);
            }, result =>
            {
                if (result.Status == ResultStatus.Completed)
                {
                    fileViewModel.FileModel = result.Data;
                }

                _systemTrayService.HideProgressIndicator();
            }, null);
        }

        private void UploadFile()
        {
            AbortCurrentCall();

            _photoChooserService.Show(true, result =>
            {
                if (result.TaskResult != Microsoft.Phone.Tasks.TaskResult.OK)
                    return;

                DispatcherHelper.RunAsync(() =>
                {
                    _mainModel.CheckTokenAndExecute<GoogleDriveFile>((authToken, callback, state) =>
                    {
                        AbortCurrentCall();

                        _systemTrayService.SetProgressIndicator("Uploading file...");

                        _asyncHandle = _googleDriveService.FilesInsert(authToken, new GoogleDriveFilesInsertRequest()
                        {
                            Filename = System.IO.Path.GetFileName(result.OriginalFileName),
                            FileContent = result.ChosenPhoto.ToArray(),
                            FolderId = _mainModel.CurrentFolderId,
                            Fields = GoogleDriveFileFields
                        }, callback, state);
                    }, result2 =>
                    {
                        switch (result2.Status)
                        {
                            case ResultStatus.Aborted:
                                break;

                            case ResultStatus.Completed:
                                _systemTrayService.HideProgressIndicator();

                                var googleFileViewModel = new GoogleFileViewModel(result2.Data);

                                Files.Add(googleFileViewModel);

                                if (!string.IsNullOrEmpty(googleFileViewModel.ThumbnailLink))
                                {
                                    PictureFiles.Add(googleFileViewModel);
                                }

                                break;

                            default:
                                _systemTrayService.HideProgressIndicator();

                                _messageBoxService.Show("Unable to upload the file!", "Error");

                                break;
                        }
                    }, null);
                });
            });
        }

        private void RefreshFiles()
        {
            var currentFolderId = _mainModel.CurrentFolderId;

            _mainModel.CheckTokenAndExecute<GoogleDriveFilesListResponse>((authToken, callback, state) =>
            {
                AbortCurrentCall();

                _systemTrayService.SetProgressIndicator("Refreshing the file list...");

                Files.Clear();
                PictureFiles.Clear();

                _asyncHandle = _googleDriveService.FilesList(authToken, new GoogleDriveFilesListRequest()
                {
                    Query = "'{0}' in parents".FormatWith(currentFolderId),
                    Fields = GoogleDriveFileFields
                }, callback, state);
            }, RefreshFilesCallback, new RefreshFilesState(currentFolderId, true));
        }

        private void RefreshFilesCallback(Result<GoogleDriveFilesListResponse> result)
        {
            switch (result.Status)
            {
                case ResultStatus.Aborted:
                    break;

                case ResultStatus.Completed:
                    var state = (RefreshFilesState)result.State;
                    var currentFolderId = state.CurrentFolderId;

                    var response = result.Data;

                    if (response.Items != null)
                    {
                        foreach (var child in response.Items)
                        {
                            var googleFileViewModel = new GoogleFileViewModel(child);

                            Files.Add(googleFileViewModel);

                            if (child != null && !string.IsNullOrEmpty(child.ThumbnailLink))
                            {
                                PictureFiles.Add(googleFileViewModel);
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(response.NextPageToken))
                    {
                        _systemTrayService.HideProgressIndicator();
                    }
                    else
                    {
                        _mainModel.CheckTokenAndExecute<GoogleDriveFilesListResponse>((authToken, callback, state2) =>
                        {
                            _asyncHandle = _googleDriveService.FilesList(authToken, new GoogleDriveFilesListRequest()
                            {
                                Query = "'{0}' in parents".FormatWith(currentFolderId),
                                Fields = GoogleDriveFileFields,
                                PageToken = response.NextPageToken
                            }, callback, state2);
                        }, RefreshFilesCallback, new RefreshFilesState(currentFolderId));
                    }

                    break;

                default:
                    _systemTrayService.HideProgressIndicator();

                    _messageBoxService.Show("Unable to update the files!", "Error");

                    break;
            }
        }

        private void CreateNewFolder()
        {
            AbortCurrentCall();

            _navigationService.NavigateTo("/View/NewFolderPage.xaml");
        }

        private void DeleteNextFile(IEnumerator<GoogleFileViewModel> enumerator)
        {
            if (enumerator.MoveNext())
            {
                var fileViewModel = enumerator.Current;
                var fileTitle = fileViewModel.Title;

                _mainModel.CheckTokenAndExecute<GoogleDriveFile>((authToken, callback, state) =>
                {
                    AbortCurrentCall();

                    _systemTrayService.SetProgressIndicator(string.Format("Deleting: {0}...", fileTitle));

                    _asyncHandle = _googleDriveService.FilesDelete(authToken, fileViewModel.Id, callback, state);
                }, result =>
                {
                    DispatcherHelper.CheckBeginInvokeOnUI(() =>
                    {
                        switch (result.Status)
                        {
                            case ResultStatus.Aborted:
                                enumerator.Dispose();

                                break;

                            case ResultStatus.Empty:
                            case ResultStatus.Completed:
                                lock (Files)
                                {
                                    if (Files.Contains(fileViewModel))
                                    {
                                        Files.Remove(fileViewModel);
                                    }
                                }

                                break;

                            default:
                                enumerator.Dispose();

                                _systemTrayService.HideProgressIndicator();

                                _messageBoxService.Show(string.Format("Unable to delete \"{0}\"!", fileTitle), "Erro");

                                break;
                        }
                    });

                    DeleteNextFile(enumerator);
                }, null);
            }
            else
            {
                enumerator.Dispose();

                _systemTrayService.HideProgressIndicator();
            }
        }

        private void AbortCurrentCall()
        {
            AbortCurrentCall(false);
        }
        private void AbortCurrentCall(bool hideSystemTray)
        {
            if (_asyncHandle == null)
            {
                return;
            }

            if (hideSystemTray)
            {
                _systemTrayService.HideProgressIndicator();
            }

            _asyncHandle.Abort();
            _asyncHandle = null;
        }

        //private class DeleteFilesState
        //{
        //    #region Properties

        //    public GoogleDriveChild CurrentFolder { get; private set; }

        //    #endregion

        //    public DeleteFilesState(GoogleDriveChild currentFolder)
        //    {
        //        CurrentFolder = currentFolder;
        //    }
        //}

        private class RefreshFilesState
        {
            #region Properties

            public bool FirstCall { get; private set; }

            public string CurrentFolderId { get; private set; }

            #endregion

            public RefreshFilesState(string currentFolderId, bool firstCall)
            {
                CurrentFolderId = currentFolderId;
                FirstCall = firstCall;
            }

            public RefreshFilesState(string currentFolderId)
                : this(currentFolderId, false)
            {
            }
        }
    }
}