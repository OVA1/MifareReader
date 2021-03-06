﻿using System;
using System.Diagnostics;
using PCSC;
using PCSC.Iso7816;

namespace MifareReaderLibriary
{
    public class MifareCard
    {
        private readonly byte _customCla = 0xFF; //command will be processed by the reader
        private readonly IIsoReader _isoReader;

        public MifareCard(IIsoReader isoReader)
        {
            if (isoReader == null)
            {
                throw new ArgumentNullException(nameof(isoReader));
            }
            _isoReader = isoReader;
        }

        public bool LoadKey(KeyStructure keyStructure, byte keyNumber, byte[] key)
        {
            var loadKeyCmd = new CommandApdu(IsoCase.Case3Short, SCardProtocol.Any)
            {
                CLA = _customCla,
                Instruction = InstructionCode.ExternalAuthenticate,
                P1 = (byte) keyStructure,
                P2 = keyNumber,
                Data = key
            };

            Debug.WriteLine("Load Authentication Keys: {0}", BitConverter.ToString(loadKeyCmd.ToArray()));
            var response = _isoReader.Transmit(loadKeyCmd);
            Debug.WriteLine("SW1 SW2 = {0:X2} {1:X2}", response.SW1, response.SW2);

            return IsSuccess(response);
        }


        public bool Authenticate(byte msb, byte lsb, KeyType keyType, byte keyNumber)
        {
            var authBlock = new GeneralAuthenticate
            {
                MSB = msb,
                LSB = lsb,
                KeyNumber = keyNumber,
                KeyType = keyType
            };

            var authKeyCmd = new CommandApdu(IsoCase.Case3Short, SCardProtocol.Any)
            {
                CLA = _customCla,
                Instruction = InstructionCode.InternalAuthenticate,
                P1 = 0x00,
                P2 = 0x00,
                Data = authBlock.ToArray()
            };

            Debug.WriteLine("General Authenticate: {0}", BitConverter.ToString(authKeyCmd.ToArray()));
            var response = _isoReader.Transmit(authKeyCmd);
            Debug.WriteLine("SW1 SW2 = {0:X2} {1:X2}", response.SW1, response.SW2);

            return response.SW1 == 0x90 && response.SW2 == 0x00;
        }

        public byte[] ReadBinary(byte msb, byte lsb, int size)
        {
            unchecked
            {
                var readBinaryCmd = new CommandApdu(IsoCase.Case2Short, SCardProtocol.Any)
                {
                    CLA = _customCla,
                    Instruction = InstructionCode.ReadBinary,
                    P1 = msb,
                    P2 = lsb,
                    Le = size
                };

                Debug.WriteLine("Read Binary (before update): {0}", BitConverter.ToString(readBinaryCmd.ToArray()));
                var response = _isoReader.Transmit(readBinaryCmd);
                Debug.WriteLine("SW1 SW2 = {0:X2} {1:X2} Data: {2}",
                    response.SW1,
                    response.SW2,
                    BitConverter.ToString(response.GetData()));

                return IsSuccess(response)
                    ? response.GetData() ?? new byte[0]
                    : null;
            }
        }

        internal bool UpdateBinary(byte msb, byte lsb, byte[] data)
        {
            if (data.Length > 16)
            {
                throw new Exception("Data length is more than 16 bytes");
            }
            if (msb > 0)
            {
                throw new Exception("msb is more than 0");
            }
            var updateBinaryCmd = new CommandApdu(IsoCase.Case4Short, SCardProtocol.T0)
            {
                CLA = _customCla,
                Instruction = InstructionCode.UpdateBinary,
                P1 = msb,
                P2 = lsb,
                Data = data
            };

            Debug.WriteLine("Update Binary: {0}", BitConverter.ToString(updateBinaryCmd.ToArray()));
            var response = _isoReader.Transmit(updateBinaryCmd);
            Debug.WriteLine("SW1 SW2 = {0:X2} {1:X2}", response.SW1, response.SW2);

            return IsSuccess(response);
        }

        private static bool IsSuccess(Response response)
        {
            return response.SW1 == (byte) SW1Code.Normal && response.SW2 == 0x00;
        }
    }
}