﻿using System;
using Ruffles.Configuration;
using Ruffles.Connections;
using Ruffles.Memory;
using Ruffles.Messaging;
using Ruffles.Utils;
using Ruffles.Collections;

namespace Ruffles.Channeling.Channels
{
	internal class UnreliableSequencedChannel : IChannel
	{
		// Incoming sequencing
		private ushort _incomingLowestAckedSequence;
		private readonly SlidingWindow<bool> _incomingAckedPackets; // Used just for duplicate detection
		private readonly object _receiveLock = new object();

		// Outgoing sequencing
		private ushort _lastOutboundSequenceNumber;
		private readonly object _sendLock = new object();

		// Channel info
		private byte channelId;
		private Connection connection;
		private MemoryManager memoryManager;
		private SocketConfig config;

		internal UnreliableSequencedChannel(byte channelId, Connection connection, SocketConfig config, MemoryManager memoryManager)
		{
			this.channelId = channelId;
			this.connection = connection;
			this.memoryManager = memoryManager;
			this.config = config;

			_incomingAckedPackets = new SlidingWindow<bool>(config.ReliabilityWindowSize);
		}

		public void CreateOutgoingMessage(ArraySegment<byte> payload, bool noMerge, ulong notificationKey)
		{
			if (payload.Count > connection.MTU)
			{
				if (Logging.CurrentLogLevel <= LogLevel.Error) Logging.LogError("Tried to send message that was too large. Use a fragmented channel instead. [Size=" + payload.Count + "] [MaxMessageSize=" + config.MaxFragments + "]");
				return;
			}

			lock (_sendLock)
			{
				// Increment the sequence number
				_lastOutboundSequenceNumber++;

				// Allocate the memory
				HeapMemory memory = memoryManager.AllocHeapMemory((uint)payload.Count + 4);

				// Write headers
				memory.Buffer[0] = HeaderPacker.Pack(MessageType.Data);
				memory.Buffer[1] = channelId;

				// Write the sequence
				memory.Buffer[2] = (byte)_lastOutboundSequenceNumber;
				memory.Buffer[3] = (byte)(_lastOutboundSequenceNumber >> 8);

				// Copy the payload
				Buffer.BlockCopy(payload.Array, payload.Offset, memory.Buffer, 4, payload.Count);

				// Allocate pointers
				HeapPointers pointers = memoryManager.AllocHeapPointers(1);

				// Point the first pointer to the memory
				pointers.Pointers[0] = memory;

				// Send the message to the router. Tell the router to dealloc the memory as the channel no longer needs it.
				ChannelRouter.SendMessage(pointers, true, connection, noMerge, memoryManager);
			}
		}

		internal HeapMemory CreateOutgoingHeartbeatMessage()
		{
			lock (_sendLock)
			{
				// Increment the sequence number
				_lastOutboundSequenceNumber++;

				// Allocate the memory
				HeapMemory memory = memoryManager.AllocHeapMemory(3);

				// Write headers
				memory.Buffer[0] = HeaderPacker.Pack(MessageType.Heartbeat);

				// Write the sequence
				memory.Buffer[1] = (byte)_lastOutboundSequenceNumber;
				memory.Buffer[2] = (byte)(_lastOutboundSequenceNumber >> 8);

				return memory;
			}
		}

		public void HandleAck(ArraySegment<byte> payload)
		{
			// Unreliable messages have no acks.
		}

		private ushort _lastPollSequence;
		public void SetLastPollSequence()
		{
			_lastPollSequence = _incomingLowestAckedSequence;
		}
		public HeapPointers HandleIncomingMessagePoll(ArraySegment<byte> payload)
		{
			// -> Sorting happens in the ConcurrentCircularQueue when messages are Enqueued

			//

			// Read the sequence number
			ushort sequence = (ushort)(payload.Array[payload.Offset] | (ushort)(payload.Array[payload.Offset + 1] << 8));

			lock (_receiveLock)
			{
				if (_incomingAckedPackets.Contains(sequence))
				{
					// We have already received this message. Ignore it.
					return null;
				}

				/* if (SequencingUtils.Distance(sequence, _incomingLowestAckedSequence, sizeof(ushort)) <= 0)
				{
					if (SequencingUtils.Distance(sequence, _lastPollSequence, sizeof(ushort)) > 0)
					{
						Logging.LogInfo("Skipping dropping packet since it's newer than _lastPollSequence (" + _lastPollSequence + ") | _incomingLowestAckedSequence: " + _incomingLowestAckedSequence + " | currentSequence: " + sequence);
					}
				} */

				if (SequencingUtils.Distance(sequence, _lastPollSequence, sizeof(ushort)) > 0)
				// ! We're using _lastPollSequence instead of _incomingLowestAckedSequence to not drop old packets if we're still waiting for them to be polled by the game/MLAPI, which can still be sorted properly
				{
					// Set the new sequence
					_incomingLowestAckedSequence = sequence;
					_incomingAckedPackets.Set(sequence, true);

					// Alloc pointers
					HeapPointers pointers = memoryManager.AllocHeapPointers(1);

					// Alloc wrapper
					pointers.Pointers[0] = memoryManager.AllocMemoryWrapper(new ArraySegment<byte>(payload.Array, payload.Offset + 2, payload.Count - 2));

					return pointers;
				}

				return null;
			}
		}

		public void InternalUpdate(out bool timeout)
		{
			// UnreliableSequenced doesnt need to resend, thus no internal loop is required
			timeout = false;
		}

		public void Release()
		{
			lock (_sendLock)
			{
				lock (_receiveLock)
				{
					// Clear all incoming states
					_incomingLowestAckedSequence = 0;
					_lastPollSequence = 0;

					// Clear all outgoing states
					_lastOutboundSequenceNumber = 0;
				}
			}
		}

		public void Assign(byte channelId, Connection connection, SocketConfig config, MemoryManager memoryManager)
		{
			lock (_sendLock)
			{
				lock (_receiveLock)
				{
					this.channelId = channelId;
					this.connection = connection;
					this.config = config;
					this.memoryManager = memoryManager;
				}
			}
		}
	}
}
