using NBitcoin;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.CoinJoin.Client.Clients.Queuing;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Services;

namespace WalletWasabi.Gui.CommandLine
{
	public class Daemon
	{
		public Daemon(Global global)
		{
			Global = global;
		}

		private Global Global { get; }

		private WalletService WalletService { get; set; }

		internal async Task RunAsync(string walletName, string destinationWalletName, bool mixAll, bool keepMixAlive)
		{
			try
			{
				Logger.LogSoftwareStarted("Wasabi Daemon");

				KeyManager keyManager = TryGetKeyManagerFromWalletName(walletName);
				if (keyManager is null)
				{
					return;
				}

				string password = null;
				var count = 3;
				string compatibilityPassword = null;
				do
				{
					if (password != null)
					{
						if (count > 0)
						{
							Logger.LogError($"Wrong password. {count} attempts left. Try again.");
						}
						else
						{
							Logger.LogCritical($"Wrong password. {count} attempts left. Exiting...");
							return;
						}
						count--;
					}
					Console.Write("Password: ");

					password = PasswordConsole.ReadPassword();
					if (PasswordHelper.IsTooLong(password, out password))
					{
						Console.WriteLine(PasswordHelper.PasswordTooLongMessage);
					}
					if (PasswordHelper.IsTrimable(password, out password))
					{
						Console.WriteLine(PasswordHelper.TrimWarnMessage);
					}
				}
				while (!PasswordHelper.TryPassword(keyManager, password, out compatibilityPassword));

				if (compatibilityPassword != null)
				{
					password = compatibilityPassword;
					Logger.LogInfo(PasswordHelper.CompatibilityPasswordWarnMessage);
				}

				Logger.LogInfo("Correct password.");

				await Global.InitializeNoWalletAsync();
				if (Global.KillRequested)
				{
					return;
				}

				WalletService = await Global.WalletManager.CreateAndStartWalletServiceAsync(keyManager);
				if (Global.KillRequested)
				{
					return;
				}

				KeyManager destinationKeyManager = TryGetKeyManagerFromWalletName(destinationWalletName);
				bool isDestinationSpecified = keyManager.ExtPubKey != destinationKeyManager.ExtPubKey;
				if (isDestinationSpecified)
				{
					await Global.WalletManager.CreateAndStartWalletServiceAsync(destinationKeyManager);
				}

				// Enqueue coins up to the anonset target, except if mixall, because then enqueue all coins.
				// However if destination is specified, then disregard this initial enqueue.
				// This way mixall will have no effect. Or more specifically the output wallet specification will mix on top of mixed coins, so the result is the same.
				if (!isDestinationSpecified)
				{
					await TryQueueCoinsToMixAsync(password, maxAnonset: mixAll ? int.MaxValue : WalletService.ServiceConfiguration.MixUntilAnonymitySet - 1);
				}

				do
				{
					if (Global.KillRequested)
					{
						break;
					}

					await Task.Delay(3000);

					if (Global.KillRequested)
					{
						break;
					}

					// If no coins enqueued then enqueue the large anonset coins and mix to another wallet.
					if (isDestinationSpecified && !AnyCoinsQueued())
					{
						WalletService.ChaumianClient.DestinationKeyManager = destinationKeyManager;
						await TryQueueCoinsToMixAsync(password, minAnonset: WalletService.ServiceConfiguration.MixUntilAnonymitySet);
					}

					// If no coins were queued then try to queue coins those have less anonset and mix it into the same wallet.
					if (!AnyCoinsQueued())
					{
						// Don't do mixall here, the mixall says all the coins has to be mixed once, it doesn't says it has to be requeued all the time.
						WalletService.ChaumianClient.DestinationKeyManager = WalletService.ChaumianClient.KeyManager;
						await TryQueueCoinsToMixAsync(password, maxAnonset: WalletService.ServiceConfiguration.MixUntilAnonymitySet - 1);
					}
				}
				// Keep this loop alive as long as a coin is queued or keepalive was specified.
				while (keepMixAlive || AnyCoinsQueued());

				await Global.DisposeAsync();
			}
			catch
			{
				if (!Global.KillRequested)
				{
					throw;
				}
			}
			finally
			{
				Logger.LogInfo($"{nameof(Daemon)} stopped.");
			}
		}

		private bool AnyCoinsQueued()
		{
			return WalletService.ChaumianClient.State.AnyCoinsQueued();
		}

		public KeyManager TryGetKeyManagerFromWalletName(string walletName)
		{
			try
			{
				KeyManager keyManager = null;
				if (walletName != null)
				{
					var walletFullPath = Global.GetWalletFullPath(walletName);
					var walletBackupFullPath = Global.GetWalletBackupFullPath(walletName);
					if (!File.Exists(walletFullPath) && !File.Exists(walletBackupFullPath))
					{
						// The selected wallet is not available any more (someone deleted it?).
						Logger.LogCritical("The selected wallet does not exist, did you delete it?");
						return null;
					}

					try
					{
						keyManager = Global.LoadKeyManager(walletFullPath, walletBackupFullPath);
					}
					catch (Exception ex)
					{
						Logger.LogCritical(ex);
						return null;
					}
				}

				if (keyManager is null)
				{
					Logger.LogCritical("Wallet was not supplied. Add --wallet:WalletName");
				}

				return keyManager;
			}
			catch (Exception ex)
			{
				Logger.LogCritical(ex);
				return null;
			}
		}

		private async Task TryQueueCoinsToMixAsync(string password, int minAnonset = int.MinValue, int maxAnonset = int.MaxValue)
		{
			try
			{
				var coinsToMix = WalletService.Coins.Available().FilterBy(x => x.AnonymitySet <= maxAnonset && minAnonset <= x.AnonymitySet);

				var enqueuedCoins = await WalletService.ChaumianClient.QueueCoinsToMixAsync(password, coinsToMix.ToArray());

				if (enqueuedCoins.Any())
				{
					Logger.LogInfo($"Enqueued {Money.Satoshis(enqueuedCoins.Sum(x => x.Amount)).ToString(false, true)} BTC, {enqueuedCoins.Count()} coins with smalles anonset {enqueuedCoins.Min(x => x.AnonymitySet)} and largest anonset {enqueuedCoins.Max(x => x.AnonymitySet)}.");
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex);
			}
		}
	}
}
