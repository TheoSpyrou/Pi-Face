using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.UI.Xaml.Shapes;

//All the necessary namespaces we added for the project to work
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Devices.Gpio;


//Project created by Theofilos Spyrou for the Student Guru Thessaloniki Community on April 2016

namespace PiFace
{
    public sealed partial class MainPage : Page
    {
        //General Properties
        private bool isPreviewing;
        //a is the ratio between the actual size of the image and the resized one (400x300)
        private double a;

        //Properties containing all the necessairy information to use Project Oxford Face API
        private const string personGroupId = "";        //Place your group name here
        private const string oxfordKey = "";            //Place your Face API key here
        private IFaceServiceClient faceServiceClient;

        //Object for getting images from camera
        private MediaCapture capture;
        //Object containing the photo file
        private StorageFile photoFile;
        private IMediaEncodingProperties previewProperties;
        private FaceDetectionEffect faceDetectionEffect;

        //Properties for playing audio playback
        private SpeechSynthesizer speech = new SpeechSynthesizer();
        private SpeechSynthesisStream speechStream;
        private MediaElement audioPlayer = new MediaElement();

        //Properties for the physical push button
        private const int BUTTON_PIN = 5;
        private GpioPin buttonPin;
        private GpioController gpio = GpioController.GetDefault();


        public MainPage()
        {
            this.InitializeComponent();

            //Event Handler for suspending the app
            Application.Current.Suspending += async delegate
            {
                CloseCamera();
                if (photoFile != null)
                {
                    await photoFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    photoFile = null;
                }
            };
            //Event Handler for resuming the app
            Application.Current.Resuming += delegate { OpenCamera(); };

            //FaceServiceClient type object for sending requests to Project Oxford Server
            faceServiceClient = new FaceServiceClient(oxfordKey);

            //If there are GPIOs
            if (gpio != null)
            {
                //Open the button pin
                buttonPin = gpio.OpenPin(BUTTON_PIN);

                //If it's supported, make button pin pull up input
                //else make it simple input
                if (buttonPin.IsDriveModeSupported(GpioPinDriveMode.InputPullUp))
                    buttonPin.SetDriveMode(GpioPinDriveMode.InputPullUp);
                else
                    buttonPin.SetDriveMode(GpioPinDriveMode.Input);

                //Least time between two successively button presses
                buttonPin.DebounceTimeout = TimeSpan.FromMilliseconds(50);
                //Event Handler that triggers every time button is pressed or released
                buttonPin.ValueChanged += buttonPin_ValueChanged;
            }
        }

        //Method for opening the camera
        private async void OpenCamera()
        {
            try
            {
                capture = new MediaCapture();
                //Initialization of the settings of the camera
                await capture.InitializeAsync();

                //Event Handler for failing to use the camera. It's triggered when an error about
                //the camera occurs or when cannot open or find any camera.
                capture.Failed += delegate { CloseCamera(); };

                //Sets the Source of the CaptureElement XAML control
                camPreview.Source = capture;
                //Mirrors the preview image
                camPreview.FlowDirection = FlowDirection.RightToLeft;

                //Starts the preview
                await capture.StartPreviewAsync();
                isPreviewing = true;
                //Indicates that we preview video
                previewProperties = capture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);

                //Let user take a photo
                takePhotoBtn.IsEnabled = true;

                faceDetectionInitialization();
            }
            catch
            {
                //If an error occurs turn off tongle button
                camStateToggle.IsOn = false;
            }
        }

        //Method for taking a photo and sending it to Project Oxford Server for processing
        private async void TakePhotoBtn_Click(object sender, RoutedEventArgs e)
        {
            //We want to disable the button during this process so that we avoid errors may occur
            //from sending multiple requests almost together if user clicks the button many times
            takePhotoBtn.IsEnabled = false;
            //Enables the progress ring
            progress.IsActive = true;
            //message contains what will be read by app
            string message = string.Empty;

            try
            {
                //Sets the encoding of the image to Jpeg
                ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
                //Creates an empty file with name rasp_photo.jpg and stores it in Pictures Library
                //If there is already a file with that name there, replace it
                photoFile = await KnownFolders.PicturesLibrary.CreateFileAsync("rasp_photo.jpg", CreationCollisionOption.ReplaceExisting);
                //Stores the current frame from camera to the file we just created
                await capture.CapturePhotoToStorageFileAsync(imageProperties, photoFile);
            }
            catch (Exception ex)
            {
                log.Text = ex.Message;
            }

            //Converts photo file to stream and destroys it right after we don't need it anymore
            using (Stream s = await photoFile.OpenStreamForReadAsync())
            {
                try
                {
                    //Asks Project Oxford to detect the faces in the photo we previously took
                    Face[] faces = await faceServiceClient.DetectAsync(s);
                    //Isolates the IDs of the detected faces to a new array
                    Guid[] faceIds = faces.Select(face => face.FaceId).ToArray();
                    IdentifyResult[] results;

                    //If there are no faces in the photo, throw Exception
                    //else ask Project Oxford to identify the faces of the photo
                    if (faces.Length == 0)
                        throw new Exception("No faces detected!");
                    else
                        results = await faceServiceClient.IdentifyAsync(personGroupId, faceIds);

                    //For every result we got...
                    foreach (var identifyResult in results)
                    {
                        //If there are no Candidates from the group we have created, throw Exception
                        if (identifyResult.Candidates.Length == 0)
                            throw new Exception("I don't know you!");

                        //Gets the ID of the most possibly candidate for the the current face
                        Guid candidateId = identifyResult.Candidates[0].PersonId;
                        //Asks for the recognized person's details
                        Person person = await faceServiceClient.GetPersonAsync(personGroupId, candidateId);
                        //Sets the message to be read
                        message = log.Text = string.Format("Welcome {0}!", person.Name);
                    }
                }
                catch (FaceAPIException ex)
                {
                    log.Text = ex.ErrorMessage;
                }
                catch (Exception ex)
                {
                    //Assigns Error's message to the string that will be read
                    message = log.Text = ex.Message;
                }
                finally
                {
                    //Re-enables the button for taking photos
                    takePhotoBtn.IsEnabled = true;
                    //Disables pogress ring
                    progress.IsActive = false;

                    //Converts string to stream so that it can be read
                    speechStream = await speech.SynthesizeTextToStreamAsync(message);
                    audioPlayer.SetSource(speechStream, speechStream.ContentType);
                    //Starts playback
                    audioPlayer.Play();
                }
            }
        }

        //Method for closing the camera
        //It disables and deletes everything relative to the camera
        private async void CloseCamera()
        {
            if (faceDetectionEffect != null && faceDetectionEffect.Enabled)
            {
                faceDetectionEffect.Enabled = false;
                faceDetectionEffect.FaceDetected -= faceDetected;
                await capture.ClearEffectsAsync(MediaStreamType.VideoPreview);
                faceDetectionEffect = null;
                facesCanvas.Children.Clear();
            }

            if (capture != null)
            {
                if (isPreviewing)
                {
                    await capture.StopPreviewAsync();
                    isPreviewing = false;
                }
                capture.Dispose();
                capture = null;
            }

            takePhotoBtn.IsEnabled = false;
        }

        private void camStateToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isPreviewing)
                CloseCamera();
            else
                OpenCamera();
        }


        //Method for initializing the settings for the boxes that will apear on each face in the preview
        //It also starts looking for faces in the preview
        private async void faceDetectionInitialization()
        {
            var definition = new FaceDetectionEffectDefinition()
            {
                SynchronousDetectionEnabled = false,
                DetectionMode = FaceDetectionMode.HighPerformance
            };
            
            faceDetectionEffect = (FaceDetectionEffect)await capture.AddVideoEffectAsync(definition, MediaStreamType.VideoPreview);

            //Chooses the shortest interval between detection events
            faceDetectionEffect.DesiredDetectionInterval = TimeSpan.FromMilliseconds(33);
            faceDetectionEffect.Enabled = true;
            faceDetectionEffect.FaceDetected += faceDetected;
        }

        //Method for drawing boxes over every face in the preview
        private async void faceDetected(FaceDetectionEffect sender, FaceDetectedEventArgs args)
        {
            var previewStream = previewProperties as VideoEncodingProperties;

            var dispatcher = Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher;

            await dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {

                a = camPreview.Width / previewStream.Width;

                facesCanvas.Children.Clear();

                //Detects faces in preview (without Project Oxford) and places a rectangle on them
                foreach (Windows.Media.FaceAnalysis.DetectedFace face in args.ResultFrame.DetectedFaces)
                {
                    Rectangle rect = new Rectangle()
                    {
                        Width = face.FaceBox.Width * a,
                        Height = face.FaceBox.Height * a,
                        Stroke = new SolidColorBrush(Windows.UI.Colors.Red),
                        StrokeThickness = 2.0
                    };

                    facesCanvas.Children.Add(rect);
                    Canvas.SetLeft(rect, camPreview.Width - (face.FaceBox.X * a) - rect.Width);
                    Canvas.SetTop(rect, face.FaceBox.Y * a);
                }
            });
        }


        private async void buttonPin_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs args)
        {
            try
            {
                //If the push button is pressed and a previous press has ended its process,
                //recognize the faces (by calling TakePhotoBtn_Click method
                if (sender.Read() == GpioPinValue.Low && takePhotoBtn.IsEnabled)
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        TakePhotoBtn_Click(null, null);
                    });
            }
            catch
            {

            }

        }
    }
}
