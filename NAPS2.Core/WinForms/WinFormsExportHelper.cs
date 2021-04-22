﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic;
using NAPS2.Config;
using NAPS2.ImportExport;
using NAPS2.ImportExport.Email;
using NAPS2.ImportExport.Images;
using NAPS2.ImportExport.Pdf;
using NAPS2.Lang.Resources;
using NAPS2.Logging;
using NAPS2.Ocr;
using NAPS2.Operation;
using NAPS2.Scan.Images;
using NAPS2.Util;

namespace NAPS2.WinForms
{
    public class WinFormsExportHelper
    {
        private readonly PdfSettingsContainer pdfSettingsContainer;
        private readonly ImageSettingsContainer imageSettingsContainer;
        private readonly EmailSettingsContainer emailSettingsContainer;
        private readonly DialogHelper dialogHelper;
        private readonly FileNamePlaceholders fileNamePlaceholders;
        private readonly ChangeTracker changeTracker;
        private readonly IOperationFactory operationFactory;
        private readonly IFormFactory formFactory;
        private readonly OcrManager ocrManager;
        private readonly IEmailProviderFactory emailProviderFactory;
        private readonly IOperationProgress operationProgress;
        private readonly IUserConfigManager userConfigManager;

        public WinFormsExportHelper(PdfSettingsContainer pdfSettingsContainer, ImageSettingsContainer imageSettingsContainer, EmailSettingsContainer emailSettingsContainer, DialogHelper dialogHelper, FileNamePlaceholders fileNamePlaceholders, ChangeTracker changeTracker, IOperationFactory operationFactory, IFormFactory formFactory, OcrManager ocrManager, IEmailProviderFactory emailProviderFactory, IOperationProgress operationProgress, IUserConfigManager userConfigManager)
        {
            this.pdfSettingsContainer = pdfSettingsContainer;
            this.imageSettingsContainer = imageSettingsContainer;
            this.emailSettingsContainer = emailSettingsContainer;
            this.dialogHelper = dialogHelper;
            this.fileNamePlaceholders = fileNamePlaceholders;
            this.changeTracker = changeTracker;
            this.operationFactory = operationFactory;
            this.formFactory = formFactory;
            this.ocrManager = ocrManager;
            this.emailProviderFactory = emailProviderFactory;
            this.operationProgress = operationProgress;
            this.userConfigManager = userConfigManager;
        }

        public async Task<bool> SavePDF(List<ScannedImage> images, ISaveNotify notify)
        {
            if (images.Any())
            {
                string savePath;

                var pdfSettings = pdfSettingsContainer.PdfSettings;
                if (pdfSettings.SkipSavePrompt && Path.IsPathRooted(pdfSettings.DefaultFileName))
                {
                    savePath = pdfSettings.DefaultFileName;
                }
                else
                {
                    if (!dialogHelper.PromptToSavePdf(pdfSettings.DefaultFileName, out savePath))
                    {
                        return false;
                    }
                }

                var changeToken = changeTracker.State;
                string firstFileSaved = await ExportPDF(savePath, images, false, null);
                if (firstFileSaved != null)
                {
                    changeTracker.Saved(changeToken);
                    notify?.PdfSaved(firstFileSaved);
                    return true;
                }
            }
            return false;
        }

        public async Task<string> ExportPDF(string filename, List<ScannedImage> images, bool email, EmailMessage emailMessage)
        {
            var op = operationFactory.Create<SavePdfOperation>();

            var pdfSettings = pdfSettingsContainer.PdfSettings;
            pdfSettings.Metadata.Creator = MiscResources.NAPS2;
            if (op.Start(filename, DateTime.Now, images, pdfSettings, ocrManager.DefaultParams, email, emailMessage))
            {
                operationProgress.ShowProgress(op);
            }
            return await op.Success ? op.FirstFileSaved : null;
        }

        public async Task<bool> SaveImages(List<ScannedImage> images, ISaveNotify notify)
        {
            if (images.Any())
            {
                string savePath;

                var imageSettings = imageSettingsContainer.ImageSettings;
                if (imageSettings.SkipSavePrompt && Path.IsPathRooted(imageSettings.DefaultFileName))
                {
                    savePath = imageSettings.DefaultFileName;
                }
                else
                {
                    if (!dialogHelper.PromptToSaveImage(imageSettings.DefaultFileName, out savePath))
                    {
                        return false;
                    }
                }

                var op = operationFactory.Create<SaveImagesOperation>();
                var changeToken = changeTracker.State;
                if (op.Start(savePath, DateTime.Now, images))
                {
                    operationProgress.ShowProgress(op);
                }
                if (await op.Success)
                {
                    changeTracker.Saved(changeToken);
                    notify?.ImagesSaved(images.Count, op.FirstFileSaved);
                    return true;
                }
            }
            return false;
        }

        public async Task<bool> EmailPDF(List<ScannedImage> images)
        {
            if (!images.Any())
            {
                return false;
            }

            if (userConfigManager.Config.EmailSetup == null)
            {
                // First run; prompt for a 
                var form = formFactory.Create<FEmailProvider>();
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return false;
                }
            }

            var emailSettings = emailSettingsContainer.EmailSettings;
            var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());
            var attachmentName = new string(emailSettings.AttachmentName.Where(x => !invalidChars.Contains(x)).ToArray());
            if (string.IsNullOrEmpty(attachmentName))
            {
                attachmentName = "Scan.pdf";
            }
            if (!attachmentName.EndsWith(".pdf", StringComparison.InvariantCultureIgnoreCase))
            {
                attachmentName += ".pdf";
            }
            attachmentName = fileNamePlaceholders.SubstitutePlaceholders(attachmentName, DateTime.Now, false);

            var tempFolder = new DirectoryInfo(Path.Combine(Paths.Temp, Path.GetRandomFileName()));
            tempFolder.Create();
            try
            {
                string targetPath = Path.Combine(tempFolder.FullName, attachmentName);
                var changeToken = changeTracker.State;

                var message = new EmailMessage();
                if (await ExportPDF(targetPath, images, true, message) != null)
                {
                    changeTracker.Saved(changeToken);
                    return true;
                }
            }
            finally
            {
                tempFolder.Delete(true);
            }
            return false;
        }

        public async Task<bool> SendPDF2LN(List<ScannedImage> images)
        {
            if (!images.Any())
            {
                // return false;
            }

            var tempFolder = new DirectoryInfo(Path.Combine(Paths.Temp, Path.GetRandomFileName()));
            var emailSettings = emailSettingsContainer.EmailSettings;
            var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());
            var attachmentName = new string(emailSettings.AttachmentName.Where(x => !invalidChars.Contains(x)).ToArray());
            if (string.IsNullOrEmpty(attachmentName))
            {
                attachmentName = "Scan.pdf";
            }
            if (!attachmentName.EndsWith(".pdf", StringComparison.InvariantCultureIgnoreCase))
            {
                attachmentName += ".pdf";
            }
            attachmentName = fileNamePlaceholders.SubstitutePlaceholders(attachmentName, DateTime.Now, false);
            tempFolder.Create();

            Object ws, uidoc, doc;
            try
            {
                var changeToken = changeTracker.State;
                string targetPath = Path.Combine(tempFolder.FullName, attachmentName);
                string pdfFileSaved = await ExportPDF(targetPath, images, false, null);
                if (pdfFileSaved != null)
                {
                    // instantiate a Notes session and workspace
                    //Type NotesSession = Type.GetTypeFromProgID("Notes.NotesSession");
                    //Object sess = Activator.CreateInstance(NotesSession);
                    Type NotesUIWorkspace = Type.GetTypeFromProgID("Notes.NotesUIWorkspace");
                    if (NotesUIWorkspace == null) throw new NullReferenceException("Not found Notes.NotesUIWorkspace");
                    ws = Activator.CreateInstance(NotesUIWorkspace);
                    if (ws == null) throw new NullReferenceException("Not found Notes.NotesUIWorkspace");
                    uidoc = NotesUIWorkspace.InvokeMember("EditDocument", BindingFlags.InvokeMethod, null, ws, new Object[] { true });
                    if (uidoc == null) throw new NullReferenceException("Not found opened document in Notes.NotesUIWorkspace");

                    Type NotesUIDocument = uidoc.GetType();
                    doc = NotesUIDocument.InvokeMember("Document", BindingFlags.GetProperty, null, uidoc, null);
                    Type NotesDocument = doc.GetType();

                    /*       rtf = NotesDocument.InvokeMember("GetFirstItem", BindingFlags.InvokeMethod, null, doc, new Object[] { "Body" });
                           Type NotesRichTextItem = rtf.GetType();
       */
                    // bring the Notes window to the front
                    String windowTitle = (String)NotesUIDocument.InvokeMember("WindowTitle", BindingFlags.GetProperty, null, uidoc, null);
                    Interaction.AppActivate(windowTitle);

                    /*        embObj = NotesRichTextItem.InvokeMember("EmbedObject", BindingFlags.InvokeMethod, null, rtf, new Object[] { 1454, "", "d:\\Download\\scan17_33_39.pdf" });
                            bool resSave = (bool)NotesDocument.InvokeMember("Save", BindingFlags.InvokeMethod, null, doc, new Object[] { true, true });
                            if (resSave)
                            {
                                changeTracker.Saved(changeToken);
                                return true;
                            }
                     */

                    StringCollection paths = new StringCollection();
                    paths.Add(@pdfFileSaved);
                    Clipboard.SetFileDropList(paths);

                    NotesUIDocument.InvokeMember("GotoField", BindingFlags.InvokeMethod, null, uidoc, new Object[] { "Body" });
                    NotesUIDocument.InvokeMember("Paste", BindingFlags.InvokeMethod, null, uidoc, null);

                    changeTracker.Saved(changeToken);
                    return true;


                }

            }
            catch (Exception ex)
            {
                Log.ErrorException(MiscResources.ErrorSaving, ex);
                MessageBox.Show(ex.Message);
            }
            finally
            {
                tempFolder.Delete(true);
                uidoc = null;
                //sess = null;
                ws = null;
            }

            return false;

        }

        public async Task<string> UploadFile(string url, string docId, string filename, Dictionary<string, object> postData)
        {
            var request = WebRequest.Create(url);
            var boundary = $"{Guid.NewGuid():N}"; // boundary will separate each parameter
            request.ContentType = $"multipart/form-data; {nameof(boundary)}={boundary}";
            request.Method = "POST";

            using (var requestStream = request.GetRequestStream())
            using (var writer = new StreamWriter(requestStream))
            {
                foreach (var data in postData)
                    await writer.WriteAsync( // put all POST data into request
                        $"\r\n--{boundary}\r\nContent-Disposition: " +
                        $"form-data; name=\"{data.Key}\"\r\n\r\n{data.Value}");

                await writer.WriteAsync( // file header
                    $"\r\n--{boundary}\r\nContent-Disposition: " +
                    $"form-data; name=\"File\"; filename=\"{docId}_{Path.GetFileName(filename)}\"\r\n" +
                    "Content-Type: application/octet-stream\r\n\r\n");

                await writer.FlushAsync();
                using (var fileStream = File.OpenRead(filename))
                    await fileStream.CopyToAsync(requestStream);

                await writer.WriteAsync($"\r\n--{boundary}--\r\n");
            }

            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var responseStream = response.GetResponseStream())
            {
                if (responseStream == null)
                    return string.Empty;
                using (var reader = new StreamReader(responseStream))
                    return await reader.ReadToEndAsync();
            }
        }
        public async Task<bool> SendPDF2SED(List<ScannedImage> images)
        {
            if (!images.Any())
            {
                // return false;
            }

            var tempFolder = new DirectoryInfo(Path.Combine(Paths.Temp, Path.GetRandomFileName()));
            var emailSettings = emailSettingsContainer.EmailSettings;
            var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());
            var attachmentName = new string(emailSettings.AttachmentName.Where(x => !invalidChars.Contains(x)).ToArray());
            if (string.IsNullOrEmpty(attachmentName))
            {
                attachmentName = "Scan.pdf";
            }
            if (!attachmentName.EndsWith(".pdf", StringComparison.InvariantCultureIgnoreCase))
            {
                attachmentName += ".pdf";
            }
            attachmentName = fileNamePlaceholders.SubstitutePlaceholders(attachmentName, DateTime.Now, false);
            tempFolder.Create();
            
            try
            {
                var changeToken = changeTracker.State;
                string targetPath = Path.Combine(tempFolder.FullName, attachmentName);
                string pdfFileSaved = await ExportPDF(targetPath, images, false, null);
                if (pdfFileSaved != null)
                {

                    // bring the Notes window to the front
                    //String windowTitle = (String)NotesUIDocument.InvokeMember("WindowTitle", BindingFlags.GetProperty, null, uidoc, null);
                    //Interaction.AppActivate(windowTitle);

                    //StringCollection paths = new StringCollection();
                    //paths.Add(@pdfFileSaved);
                    //Clipboard.SetFileDropList(paths);
                    string args = "sed://____5fe2d87ab89808114c587d57__6849c76d7f63112c18ccda394a4f5500__ff65cedc-5fa0-44f9-bef4-99b6606ffa60____60811da30f5dda0c64d3b39b____10.75.113.107____/";
                    string[] stringSeparators = new string[] { "____" };
                    string[] subArgs = args.Split(stringSeparators, StringSplitOptions.None);
                    string docId = subArgs[2]; //"60811da30f5dda0c64d3b39b";
                    var response = await UploadFile("http://" + subArgs[3] + ":8080" + "/api/document/uploadFile", docId, pdfFileSaved, 
                        new Dictionary<string, object>  {}
                        );
                    

                    changeTracker.Saved(changeToken);
                    return true;
                }

            }
            catch (Exception ex)
            {
                Log.ErrorException(MiscResources.ErrorSaving, ex);
                MessageBox.Show(ex.Message);
            }
            finally
            {
                tempFolder.Delete(true);
            }

            return false;

        }

    }
}
