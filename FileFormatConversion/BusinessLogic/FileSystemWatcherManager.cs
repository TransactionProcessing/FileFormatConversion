using Azure.Core.Pipeline;
using Shared.Logger;
using Syncfusion.HtmlConverter;
using Syncfusion.Pdf;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using SimpleResults;

namespace FileFormatConversion.BusinessLogic
{
    public interface IFileSystemWatcherManager{
        void InitialiseManger();
        void StartFileSystemWatchers();
    }
    public class FileSystemWatcherManager : IFileSystemWatcherManager
    {
        private readonly IFileHandler FileHandler;
        public FileSystemWatcherManager(IFileHandler fileHandler) {
            this.FileHandler = fileHandler;
            this.FileSystemWatchers = new List<TaggedFileSystemWatcher>();
    }

        private readonly List<TaggedFileSystemWatcher> FileSystemWatchers;

        public void InitialiseManger() {
            // Load up the file configuration
            IEnumerable<FileProfile> fileProfiles = Startup.Configuration.GetSection("AppSettings:FileProfiles").GetChildren().ToList().Select(x => new
            {
                Name = x.GetValue<String>("Name"),
                ListeningDirectory = x.GetValue<String>("ListeningDirectory"),
                OutputDirectory = x.GetValue<String>("OutputDirectory"),
                Filter = x.GetValue<String>("Filter"),
            }).Select(f => new FileProfile(f.Name, f.ListeningDirectory, f.OutputDirectory, f.Filter));

            foreach (FileProfile fileProfile in fileProfiles) {
                this.SetupFileSystemWatcher(fileProfile);
            }
        }

        public void StartFileSystemWatchers() {
            foreach (TaggedFileSystemWatcher fsw in this.FileSystemWatchers) {
                Logger.LogInformation($"About to start File System Watcher {fsw.Tag}");
                fsw.Start();
                Logger.LogInformation($"Started File System Watcher {fsw.Tag}");
            }
        }

        public void SetupFileSystemWatcher(FileProfile fileProfile) {
            Logger.LogInformation($"About to Setup FileSystemWatcher for {fileProfile.Name}");

            // make sure all the directories exist
            CreateDirectoryWithLogging(fileProfile.ListeningDirectory);
            CreateDirectoryWithLogging($"{fileProfile.ListeningDirectory}\\processed");
            CreateDirectoryWithLogging($"{fileProfile.ListeningDirectory}\\failed");
            CreateDirectoryWithLogging(fileProfile.OutputDirectory);
            CreateDirectoryWithLogging($"{fileProfile.OutputDirectory}\\processed");
            CreateDirectoryWithLogging($"{fileProfile.OutputDirectory}\\failed");

            // Create the file system watcher
            FileSystemWatcher fsw = new(fileProfile.ListeningDirectory, fileProfile.Filter);
            fsw.Created += (sender,
                            e) => this.FileHandler.FileCreated(sender, e, fileProfile);
            fsw.Error += (sender,
                          e) => this.FileHandler.FileError(sender, e, fileProfile);
            TaggedFileSystemWatcher taggedFileSystemWatcher = new TaggedFileSystemWatcher(fsw, fileProfile.Name);
            this.FileSystemWatchers.Add(taggedFileSystemWatcher);
        }

        static void CreateDirectoryWithLogging(String directory) {
            // Check if the directory exists, if not create it
            if (!Directory.Exists(directory)) {
                Logger.LogInformation($"Directory {directory} does not exist, creating it now.");
                // Create the directory
                Directory.CreateDirectory(directory);
            }
        }
    }

    public class TaggedFileSystemWatcher
    {
        public string Tag { get; }
        public FileSystemWatcher Watcher { get; }

        public TaggedFileSystemWatcher(FileSystemWatcher watcher, string tag)
        {
            Tag = tag;
            Watcher = watcher;
        }

        public void Start() => Watcher.EnableRaisingEvents = true;
        public void Stop() => Watcher.EnableRaisingEvents = false;
    }

    public interface IFileHandler {
        void FileCreated(object sender,
                         FileSystemEventArgs e,
                         FileProfile fileProfile);

        void FileError(object sender,
                       ErrorEventArgs e,
                       FileProfile fileProfile);
    }
    public class FileHandler : IFileHandler {
        private readonly IPDFGenerator PdfGenerator;

        public FileHandler(IPDFGenerator pdfGenerator) {
            this.PdfGenerator = pdfGenerator;
        }
        public void FileCreated(object sender,
                         FileSystemEventArgs e,
                         FileProfile fileProfile) {
            String fileName = Path.GetFileNameWithoutExtension(e.FullPath);
            Logger.LogWarning($"File detected {fileName} on file profile {fileProfile.Name}.");
            String contents = File.ReadAllText(e.FullPath);
            Logger.LogInformation("About to create PDF");
            Result<String> createPdfResult = this.PdfGenerator.CreatePDF(contents);
            if (createPdfResult.IsFailed) {
                // Move the file to the failed directory
                Logger.LogWarning($"Failed to create PDF: {createPdfResult.Message}");
                File.Move(e.FullPath, Path.Combine($"{fileProfile.ListeningDirectory}\\Failed", e.Name));
                Logger.LogInformation($"Moved file {e.FullPath} to {fileProfile.ListeningDirectory}\\Failed");
                return;
            }
            Logger.LogInformation("PDF Created as base64 string");
            File.WriteAllText($"{fileProfile.OutputDirectory}\\{fileName}", createPdfResult.Data);
            Logger.LogInformation($"Output file written to {fileProfile.OutputDirectory}\\{fileName}");
            // Move the processed file
            File.Move(e.FullPath, Path.Combine($"{fileProfile.ListeningDirectory}\\Processed", e.Name));
            Logger.LogInformation($"Moved file {e.FullPath} to {fileProfile.ListeningDirectory}\\Processed");
        }

        public void FileError(object sender,
                              ErrorEventArgs e,
                              FileProfile fileProfile) {
            // TODO: Log the error
        }
    }

    public interface IPDFGenerator
    {
        #region Methods

        Result<String> CreatePDF(String htmlString);

        #endregion
    }

    [ExcludeFromCodeCoverage]
    public class PDFGenerator : IPDFGenerator {
        private readonly HtmlToPdfConverter Converter;
        public PDFGenerator() {
            BlinkConverterSettings settings = new BlinkConverterSettings
            {
                EnableJavaScript = false
            };
            settings.CommandLineArguments.Add("--headless");
            this.Converter = new HtmlToPdfConverter(HtmlRenderingEngine.Blink) { ConverterSettings = settings };
        }

        #region Methods
        public Result<String> CreatePDF(String htmlString)
        {
            try {
                PdfDocument? pdf = this.Converter.Convert(htmlString, "");
                MemoryStream stream = new();
                pdf.Save(stream);
                pdf.Close();
                String base64 = Convert.ToBase64String(stream.ToArray());
                return Result.Success(base64);
            }
            catch (Exception ex) {
                return Result.Failure(ex.Message);
            }
        }

        #endregion
    }
}
