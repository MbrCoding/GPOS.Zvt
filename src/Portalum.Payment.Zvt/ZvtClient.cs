﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Portalum.Payment.Zvt.Helpers;
using Portalum.Payment.Zvt.Models;
using Portalum.Payment.Zvt.Repositories;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Portalum.Payment.Zvt
{
    /// <summary>
    /// ZVT Protocol Client
    /// </summary>
    public class ZvtClient : IDisposable
    {
        //https://www.terminalhersteller.de/downloads/PA00P016_04_en.pdf
        //https://www.terminalhersteller.de/downloads/PA00P015_13.09_final_en.pdf

        private readonly ILogger<ZvtClient> _logger;
        private readonly byte[] _passwordData;

        private readonly ZvtCommunication _zvtCommunication;
        private readonly ReceiveHandler _receiveHandler;

        public event Action<StatusInformation> StatusInformationReceived;
        public event Action<string> IntermediateStatusInformationReceived;
        public event Action<PrintLineInfo> LineReceived;
        public event Action<ReceiptInfo> ReceiptReceived;

        /// <summary>
        /// ZvtClient
        /// </summary>
        /// <param name="deviceCommunication"></param>
        /// <param name="logger"></param>
        /// <param name="password">The password of the PT device</param>
        public ZvtClient(
            IDeviceCommunication deviceCommunication,
            ILogger<ZvtClient> logger = default,
            int password = 000000)
        {
            if (logger == null)
            {
                logger = new NullLogger<ZvtClient>();
            }
            this._logger = logger;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            this._passwordData = NumberHelper.IntToBcd(password);

            IErrorMessageRepository errorMessageRepository = new EnglishErrorMessageRepository();

            this._receiveHandler = new ReceiveHandler(logger, errorMessageRepository);
            this._receiveHandler.IntermediateStatusInformationReceived += this.ProcessIntermediateStatusInformationReceived;
            this._receiveHandler.StatusInformationReceived += this.ProcessStatusInformationReceived;
            this._receiveHandler.LineReceived += this.ProcessLineReceived;
            this._receiveHandler.ReceiptReceived += this.ProcessReceiptReceived;

            this._zvtCommunication = new ZvtCommunication(logger, deviceCommunication);
            this._zvtCommunication.DataReceived += this.DataReceived;
        }

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this._zvtCommunication.DataReceived -= this.DataReceived;
                this._zvtCommunication.Dispose();

                this._receiveHandler.IntermediateStatusInformationReceived -= this.ProcessIntermediateStatusInformationReceived;
                this._receiveHandler.StatusInformationReceived -= this.ProcessStatusInformationReceived;
                this._receiveHandler.LineReceived -= this.ProcessLineReceived;
                this._receiveHandler.ReceiptReceived -= this.ProcessReceiptReceived;
            }
        }

        private void ProcessIntermediateStatusInformationReceived(string message)
        {
            this.IntermediateStatusInformationReceived?.Invoke(message);
        }

        private void ProcessStatusInformationReceived(StatusInformation statusInformation)
        {
            this.StatusInformationReceived?.Invoke(statusInformation);
        }

        private void ProcessLineReceived(PrintLineInfo printLineInfo)
        {
            this.LineReceived?.Invoke(printLineInfo);
        }

        private void ProcessReceiptReceived(ReceiptInfo receiptInfo)
        {
            this.ReceiptReceived?.Invoke(receiptInfo);
        }

        private void DataReceived(byte[] data)
        {
            if (!this._receiveHandler.ProcessData(data))
            {
                this._logger.LogError($"{nameof(DataReceived)} - Unprocessable data received {BitConverter.ToString(data)}");
            }
        }

        private async Task<bool> SendCommandAsync(byte[] commandData, int commandResultTimeout = 90000)
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var successful = false;

            void completionReceived()
            {
                successful = true;
                cancellationTokenSource.Cancel();
            }

            void abortReceived(string errorMessage)
            {
                cancellationTokenSource.Cancel();
            }

            try
            {
                this._receiveHandler.CompletionReceived += completionReceived;
                this._receiveHandler.AbortReceived += abortReceived;

                this._logger.LogDebug($"{nameof(SendCommandAsync)} - Send command to PT");

                if (!await this._zvtCommunication.SendCommandAsync(commandData))
                {
                    this._logger.LogError($"{nameof(SendCommandAsync)} - Failure on send command");
                    return false;
                }

                await Task.Delay(commandResultTimeout, cancellationTokenSource.Token).ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        this._logger.LogError($"{nameof(SendCommandAsync)} - No result received in the specified timeout {commandResultTimeout}ms");
                    }
                });
            }
            finally
            {
                this._receiveHandler.AbortReceived -= abortReceived;
                this._receiveHandler.CompletionReceived -= completionReceived;
            }

            return successful;
        }

        private byte GetConfigByte(RegistrationConfig registrationConfig)
        {
            _ = registrationConfig ?? throw new ArgumentNullException(nameof(registrationConfig));

            var configByte = new byte();
            if (!registrationConfig.ReceiptPrintoutForPaymentFunctionsViaPaymentTerminal)
            {
                configByte = BitHelper.SetBit(configByte, 1);
            }
            if (!registrationConfig.ReceiptPrintoutForAdministrationFunctionsViaPaymentTerminal)
            {
                configByte = BitHelper.SetBit(configByte, 2);
            }
            if (registrationConfig.SendIntermediateStatusInformation)
            {
                configByte = BitHelper.SetBit(configByte, 3);
            }
            if (!registrationConfig.AllowStartPaymentViaPaymentTerminal)
            {
                configByte = BitHelper.SetBit(configByte, 4);
            }
            if (!registrationConfig.AllowAdministrationViaPaymentTerminal)
            {
                configByte = BitHelper.SetBit(configByte, 5);
            }

            configByte = BitHelper.SetBit(configByte, 7); //ECR print-type

            return configByte;
        }

        private byte[] CreatePackage(byte[] controlField, IEnumerable<byte> packageData)
        {
            var package = new List<byte>();
            package.AddRange(controlField);
            package.Add((byte)packageData.Count());
            package.AddRange(packageData);
            return package.ToArray();
        }

        /// <summary>
        /// Registration (06 00)
        /// Using the command Registration the ECR can set up different configurations on the PT and also control the current status of the PT.
        /// </summary>
        /// <param name="registrationConfig"></param>
        /// <returns></returns>
        public async Task<bool> RegistrationAsync(RegistrationConfig registrationConfig)
        {
            var configByte = this.GetConfigByte(registrationConfig);

            var package = new List<byte>();
            package.AddRange(this._passwordData);
            package.Add(configByte);

            //Currency Code (CC)
            package.AddRange(new byte[] { 0x09, 0x78 }); //Set curreny to Euro

            //Service byte
            package.Add(0x03); //Service byte indicator
            package.Add(0x00); //Service byte data

            //Add empty TLV Container
            package.Add(0x06); //TLV
            package.Add(0x00); //TLV-Length

            //TLV TAG
            //10 - Number of columns and number of lines of the merchant-display
            //11 - Number of columns and number of lines of the customer-display
            //12 - Number of characters per line of the printer
            //14 - ISO-Character set
            //1A - Max length the APDU
            //26 - List of permitted ZVT commands
            //27 - List of supported character-sets
            //28 - List of supported languages
            //29 - List of menus which should be displayed over the ECR or on a second customer-display
            //2A - List of menus which the ECR will not display and therefore must be displayed on the PT
            //40 - EMV-configuration-parameter
            //1F04 - Receipt parameter
            //1F05 - Transaction parameter

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x00 }, package);
            return await this.SendCommandAsync(fullPackage);
        }

        /// <summary>
        /// Authorization (06 01)
        /// Payment process and transmits the amount from the ECR to PT.
        /// </summary>
        /// <param name="amount"></param>
        /// <returns></returns>
        public async Task<bool> PaymentAsync(decimal amount)
        {
            this._logger.LogInformation($"{nameof(PaymentAsync)} - Start payment process, with amount of:{amount}");

            var package = new List<byte>();
            package.Add(0x04); //Amount prefix
            package.AddRange(NumberHelper.DecimalToBcd(amount));

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x01 }, package);
            return await this.SendCommandAsync(fullPackage);
        }

        /// <summary>
        /// Reversal (06 30)
        /// This command reverses a payment-procedure and transfers the receipt-number of the transaction to be reversed from the ECR to PT.
        /// The result of the reversal-process is sent to the ECR after Completion of the booking-process.
        /// </summary>
        /// <param name="receiptNumber">four-digit number</param>
        /// <returns></returns>
        public async Task<bool> ReversalAsync(int receiptNumber)
        {
            var package = new List<byte>();
            package.AddRange(this._passwordData);
            package.Add(0x87); //Password prefix
            package.AddRange(NumberHelper.IntToBcd(receiptNumber, 2));

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x30 }, package);
            return await this.SendCommandAsync(fullPackage);
        }

        /// <summary>
        /// Refund (06 31)
        /// This command starts a Refund on the PT. The result of the Refund is reported to the ECR after completion of the booking-process.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> RefundAsync()
        {
            var package = new List<byte>();
            package.AddRange(this._passwordData);

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x31 }, package);
            return await this.SendCommandAsync(fullPackage);
        }

        /// <summary>
        /// End-of-Day (06 50)
        /// ECR induces the PT to transfer the stored turnover to the host.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> EndOfDayAsync()
        {
            var package = new List<byte>();
            package.AddRange(this._passwordData);

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x50 }, package);
            return await this.SendCommandAsync(fullPackage);
        }

        /// <summary>
        /// Send Turnover Totals (06 10)
        /// With this command the ECR causes the PT to send an overview about the stored transactions.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> SendTurnoverTotalsAsync()
        {
            var package = new List<byte>();
            package.AddRange(this._passwordData);

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x10 }, package);
            return await this.SendCommandAsync(fullPackage);
        }

        /// <summary>
        /// Repeat Receipt (06 20)
        /// This command serves to repeat printing of the last stored payment-receipts or End-of-Day-receipt.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> RepeatLastReceiptAsync()
        {
            var package = new List<byte>();
            package.AddRange(this._passwordData);

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x20 }, package);
            return await this.SendCommandAsync(fullPackage);
        }

        /// <summary>
        /// Log-Off (06 02)
        /// </summary>
        /// <returns></returns>
        public async Task<bool> LogOffAsync()
        {
            var package = new List<byte>();

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x02 }, package);
            return await this.SendCommandAsync(fullPackage);
        }

        /// <summary>
        /// Diagnosis (06 70)
        /// </summary>
        /// <returns></returns>
        public async Task<bool> DiagnosisAsync()
        {
            var package = new List<byte>();

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x70 }, package);
            return await this.SendCommandAsync(fullPackage);
        }
    }
}