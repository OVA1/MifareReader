﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCSC;
using PCSC.Iso7816;

namespace MifareReaderLibriary
{
    public class MifareCardReader : IRFCardReader, IMifareCardWriter
    {
        private MifareConfiguration _config;

        private ISCardContext _context;
        private string _readerName;

        public MifareCardReader(MifareConfiguration config)
        {
            _config = config;
        }

        public async Task<bool> WriteDataAsync(ICollection<MifareKey> keys, string data, CancellationToken ct)
        {
            var contextFactory = ContextFactory.Instance;
            var context = contextFactory.Establish(SCardScope.System);
            var readerNames = context.GetReaders();
            if (NoReaderAvailable(readerNames))
            {
                throw new Exception("No readers available");
            }

            var readerName = ChooseReader(readerNames);
            var monitor = new SCardMonitor(contextFactory, SCardScope.System);
            var tcs = new TaskCompletionSource<CardStatusEventArgs>();
            ct.Register(() => { tcs.TrySetCanceled(); });
            CardInsertedEvent onCardInserted = (sender, args) => { tcs.TrySetResult(args); };
            monitor.CardInserted += onCardInserted;
            monitor.Start(new[] { readerName });
            var cardStatus = await tcs.Task.ConfigureAwait(false);
            if (!IsValidATR(cardStatus.Atr))
            {
                throw new Exception("Invalid card type");
            }

            using (var isoReader = new IsoReader(context, readerName, SCardShareMode.Shared, SCardProtocol.Any, false))
            {
                var card = new MifareCard(isoReader);

                var loadKeySuccessful = card.LoadKey(
                    KeyStructure.NonVolatileMemory,
                    0x00, // first key slot
                    keys.First().Key // key
                );

                if (!loadKeySuccessful)
                {
                    throw new Exception("LOAD KEY failed.");
                }

                var bytesToWrite = Encoding.UTF8.GetBytes(data);
                if (bytesToWrite.Length > 16 * 16 * 3)
                {
                    throw new Exception($"Data length is more than {16 * 16 * 3}");
                }

                //read all blocks except 0 sector and trailers
                for (byte i = 1; i < 16; i++)
                {
                    for (byte j = 0; j < 3; j++)
                    {
                        var currentBlock = (byte) (i * 4 + j);
                        if (CheckIfTrailerBlock(currentBlock))
                        {
                            throw new Exception("Trying to write to trailer block");
                        }
                        //auth in every block
                        var authSuccessful = card.Authenticate(0, currentBlock, KeyType.KeyA, 0x00);
                        if (!authSuccessful)
                        {
                            throw new Exception("AUTHENTICATE failed.");
                        }
                        Debug.WriteLine($"Authentification to block {currentBlock} succeded");

                        var dataToWrite = bytesToWrite.Skip((currentBlock-4) * 16).Take(16).ToArray();
                        var blockToWrite = new byte[16];
                        Array.Copy(dataToWrite, blockToWrite, dataToWrite.Length);
                        var updateResult = card.UpdateBinary(0, currentBlock, blockToWrite);
                        if (updateResult == false)
                        {
                            //fail to get block
                            Debug.WriteLine($"Fail to write block {currentBlock}");
                            return false;
                        }
                        else
                        {
                            return true;
                        }
                    }
                }
                return true;
            }
        }

        private bool CheckIfTrailerBlock(byte currentBlock)
        {
            return (currentBlock + 1) % 4 == 0;
        }

        public Task<bool> ChangeTrailersAsync(IDictionary<MifareKey, SectorTrailer> data, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public void StartScan()
        {
            var contextFactory = ContextFactory.Instance;
            _context = contextFactory.Establish(SCardScope.System);
            var readerNames = _context.GetReaders();
            if (NoReaderAvailable(readerNames))
            {
                throw new Exception("No readers available");
            }

            _readerName = ChooseReader(readerNames);
            var monitor = new SCardMonitor(contextFactory, SCardScope.System);
            monitor.CardInserted += OnCardInserted;
            monitor.Start(new[] {_readerName});
        }

        public void StopScan()
        {
            throw new NotImplementedException();
        }

        public event EventHandler CardRegistered;

        private string ATRToHexStr(byte[] atr)
        {
            var hexStrBuilder = new StringBuilder();
            foreach (var b in atr)
            {
                hexStrBuilder.AppendLine(Convert.ToString(b, 16).PadLeft(2, '0'));
            }
            return hexStrBuilder.ToString();
        }

        private void OnCardInserted(object sender, CardStatusEventArgs e)
        {
            if (!IsValidATR(e.Atr))
            {
                Debug.WriteLine("Card with invalid ATR inserted");
                return;
            }
            Debug.WriteLine("Card with valid ATR inserted");
            using (var isoReader = new IsoReader(_context, _readerName, SCardShareMode.Shared, SCardProtocol.Any, false))
            {
                var card = new MifareCard(isoReader);
                var loadKeySuccessful = card.LoadKey(
                    KeyStructure.NonVolatileMemory,
                    0x00, // first key slot
                    _config.AuthKeys[0].Key // key
                );

                if (!loadKeySuccessful)
                {
                    //key load failed
                    Debug.WriteLine("Auth key load failed");
                    return;
                }
                Debug.WriteLine("Auth key load success");

                var cardDump = new StringBuilder();

                //read all blocks except 0 sector and trailers
                for (byte i = 1; i < 16; i++)
                {
                    for (byte j = 0; j < 3; j++)
                    {
                        var currentBlock = (byte) (i * 4 + j);
                        //auth in every block
                        var authSuccessful = card.Authenticate(0, currentBlock, KeyType.KeyA, 0x00);
                        if (!authSuccessful)
                        {
                            //autentication failed
                            Debug.WriteLine($"Authentification to block {currentBlock} with key number {0} failed");
                            return;
                        }
                        Debug.WriteLine($"Authentification to block {currentBlock} succeded");

                        var result = card.ReadBinary(0, currentBlock, 16);
                        if (result == null)
                        {
                            //fail to get block
                            Debug.WriteLine($"Fail to get block {currentBlock}");
                            return;
                        }
                        var currentBlockString = Encoding.UTF8.GetString(result);
                        if (!currentBlockString.Contains('\0'))
                        {
                            cardDump.Append(currentBlockString);
                        }
                        else
                        {
                            //end of usefull content
                            cardDump.Append(currentBlockString.TrimEnd('\0'));
                            var resultString = cardDump.ToString();
                            Debug.WriteLine($"Reading complete on block {currentBlock}. Result is {resultString}");
                            if (!string.IsNullOrEmpty(resultString))
                            {
                                CardRegistered?.Invoke(this, new CardRegisteredEventArgs(resultString));
                            }
                            return;
                        }
                    }
                }
            }
        }

        private static bool NoReaderAvailable(ICollection<string> readerNames)
        {
            return readerNames == null || readerNames.Count < 1;
        }

        private string ChooseReader(string[] readerNames)
        {
            return readerNames[1];
        }

        private bool IsValidATR(byte[] cardStatusAtr)
        {
            if (cardStatusAtr[cardStatusAtr.Length - 6] == 1) //1 for mifare 1k
            {
                return true;
            }
            return false;
        }
    }
}