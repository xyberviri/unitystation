﻿using System.Collections;
using System.Collections.Generic;
using Chemistry;
using UnityEngine;
using UnityEngine.Serialization;

namespace Chemistry.Components
{
	public struct TransferResult
	{
		public bool Success;
		public string Message;
		public float TransferAmount;
		public ReagentMix Excess;
	}

	public enum TransferMode
	{
		Normal = 0, //Output from your hand, unless other thing is physically larger
		Syringe = 1, //Outputs if it's full, Inputs if it's empty
		OutputOnly = 2,
		InputOnly = 3,
		NoTransfer = 4
	}

	public partial class ReagentContainer :
		ICheckedInteractable<HandApply>, //Transfer: active hand <-> object in the world
		ICheckedInteractable<HandActivate>, //Activate to change transfer amount
		ICheckedInteractable<InventoryApply> //Transfer: active hand <-> other hand
	{
		[Header("Transfer settings")]

		[Tooltip("If not empty, another container should have one of this traits to interact")]
		[FormerlySerializedAs("TraitWhitelist")]
		[FormerlySerializedAs("AcceptedTraits")]
		[SerializeField] private List<ItemTrait> traitWhitelist;

		[Tooltip("If not empty, only listed reagents can be inside container")]
		[FormerlySerializedAs("ReagentWhitelist")]
		[FormerlySerializedAs("AcceptedReagents")]
		[SerializeField] private List<Chemistry.Reagent> reagentWhitelist;

		[FormerlySerializedAs("TransferMode")]
		[SerializeField] private TransferMode transferMode = TransferMode.Normal;

		[FormerlySerializedAs("PossibleTransferAmounts")]
		[SerializeField] private List<float> possibleTransferAmounts;

		[Range(1, 100)]
		[FormerlySerializedAs("TransferAmount")]
		[FormerlySerializedAs("InitialTransferAmount")]
		[SerializeField] private float transferAmount = 20;

		public bool TraitWhitelistOn => traitWhitelist.Count > 0;

		public bool ReagentWhitelistOn => reagentWhitelist != null && reagentWhitelist.Count > 0;

		public float TransferAmount
		{
			get => transferAmount;
			set => transferAmount = value;
		}

		public bool WillInteract(InventoryApply interaction, NetworkSide side)
		{
			if (!DefaultWillInteract.Default(interaction, side))
			{
				return false;
			}

			return WillInteractHelp(interaction.UsedObject, interaction.TargetObject, side);
		}

		public bool WillInteract(HandApply interaction, NetworkSide side)
		{
			if (!DefaultWillInteract.Default(interaction, side)) return false;

			var playerScript = interaction.Performer.GetComponent<PlayerScript>();
			if (!playerScript) return false;

			if (interaction.Intent == Intent.Help)
			{
				//checks if it's possible to transfer from container to container
				if (!WillInteractHelp(interaction.HandObject, interaction.TargetObject, side)) return false;
			}
			else
			{
				//checks if it's possible to spill contents on player
				if (!WillInteractHarm(interaction.HandObject, interaction.TargetObject, side)) return false;
			}

			return true;
		}

		private bool WillInteractHarm(GameObject srcObject, GameObject dstObject, NetworkSide side)
		{
			if (srcObject == null || dstObject == null) return false;

			var srcContainer = srcObject.GetComponent<ReagentContainer>();

			if (srcContainer == null) return false;

			if (srcContainer.transferMode == TransferMode.NoTransfer) return false;

			if (dstObject.GetComponent<PlayerScript>() == null) return false;

			return true;
		}

		private bool WillInteractHelp(GameObject srcObject, GameObject dstObject, NetworkSide side)
		{
			if (srcObject == null || dstObject == null) return false;

			var srcContainer = srcObject.GetComponent<ReagentContainer>();
			var dstContainer = dstObject.GetComponent<ReagentContainer>();

			if (srcContainer == null || dstContainer == null) return false;

			if (srcContainer.transferMode == TransferMode.NoTransfer
				|| dstContainer.transferMode == TransferMode.NoTransfer)
			{
				return false;
			}

			if (side == NetworkSide.Server)
			{
				if (srcContainer.TraitWhitelistOn && !Validations.HasAnyTrait(dstObject, srcContainer.traitWhitelist))
				{
					return false;
				}

				if (dstContainer.TraitWhitelistOn && !Validations.HasAnyTrait(dstObject, srcContainer.traitWhitelist))
				{
					return false;
				}
			}

			return dstContainer.transferMode != TransferMode.Syringe;
		}

		public void ServerPerformInteraction(InventoryApply interaction)
		{
			var one = interaction.UsedObject.GetComponent<ReagentContainer>();
			var two = interaction.TargetObject.GetComponent<ReagentContainer>();

			ServerTransferInteraction(one, two, interaction.Performer);
		}

		public void ServerPerformInteraction(HandApply interaction)
		{
			var srcPlayer = interaction.Performer.GetComponent<PlayerScript>();

			if (interaction.Intent == Intent.Help)
			{
				var one = interaction.HandObject.GetComponent<ReagentContainer>();
				var two = interaction.TargetObject.GetComponent<ReagentContainer>();

				ServerTransferInteraction(one, two, interaction.Performer);
			}
			else
			{
				//TODO: Move this to Spill right click interaction? Need to make 'RequestSpill'
				var dstPlayer = interaction.TargetObject.GetComponent<PlayerScript>();
				ServerSpillInteraction(this, srcPlayer, dstPlayer);
			}
		}

		private void ServerSpillInteraction(ReagentContainer reagentContainer, PlayerScript srcPlayer,
			PlayerScript dstPlayer)
		{
			if (reagentContainer.IsEmpty)
			{
				return;
			}

			SpillAll(dstPlayer.WorldPos);
		}

		public bool WillInteract(HandActivate interaction, NetworkSide side)
		{
			if (!DefaultWillInteract.Default(interaction, side)) return false;

			return possibleTransferAmounts.Count != 0;
		}

		public void ServerPerformInteraction(HandActivate interaction)
		{
			var currentIndex = possibleTransferAmounts.IndexOf(TransferAmount);
			if (currentIndex != -1)
			{
				TransferAmount = possibleTransferAmounts.Wrap(currentIndex + 1);
			}
			else
			{
				TransferAmount = possibleTransferAmounts[0];
			}

			Chat.AddExamineMsg(interaction.Performer,
				$"The {gameObject.ExpensiveName()}'s transfer amount is now {TransferAmount} units.");
		}


		/// <summary>
		/// Server side only
		/// Transfers Reagents between two containers
		/// </summary>
		private void ServerTransferInteraction(ReagentContainer one, ReagentContainer two, GameObject performer)
		{
			ReagentContainer transferTo = null;
			switch (one.transferMode)
			{
				case TransferMode.Normal:
					switch (two.transferMode)
					{
						case TransferMode.Normal:
							transferTo = two;
							break;
						case TransferMode.OutputOnly:
							transferTo = one;
							break;
						case TransferMode.InputOnly:
							transferTo = two;
							break;
						default:
							Logger.LogErrorFormat("Invalid transfer mode when attempting transfer {0}<->{1}",
								Category.Chemistry, one, two);
							break;
					}

					break;
				case TransferMode.Syringe:
					switch (two.transferMode)
					{
						case TransferMode.Normal:
							transferTo = one.IsFull ? two : one;
							break;
						case TransferMode.OutputOnly:
							transferTo = one;
							break;
						case TransferMode.InputOnly:
							transferTo = two;
							break;
						default:
							Logger.LogErrorFormat("Invalid transfer mode when attempting transfer {0}<->{1}",
								Category.Chemistry, one, two);
							break;
					}

					break;
				case TransferMode.OutputOnly:
					switch (two.transferMode)
					{
						case TransferMode.Normal:
							transferTo = two;
							break;
						case TransferMode.OutputOnly:
							Chat.AddExamineMsg(performer, "Both containers are output-only.");
							break;
						case TransferMode.InputOnly:
							transferTo = two;
							break;
						default:
							Logger.LogErrorFormat("Invalid transfer mode when attempting transfer {0}<->{1}",
								Category.Chemistry, one, two);
							break;
					}

					break;
				case TransferMode.InputOnly:
					switch (two.transferMode)
					{
						case TransferMode.Normal:
							transferTo = one;
							break;
						case TransferMode.OutputOnly:
							transferTo = one;
							break;
						case TransferMode.InputOnly:
							Chat.AddExamineMsg(performer, "Both containers are input-only.");
							break;
						default:
							Logger.LogErrorFormat("Invalid transfer mode when attempting transfer {0}<->{1}",
								Category.Chemistry, one, two);
							break;
					}

					break;
				default:
					Logger.LogErrorFormat("Invalid transfer mode when attempting transfer {0}<->{1}", Category.Chemistry,
						one,
						two);
					break;
			}

			if (transferTo == null)
			{
				return;
			}

			var transferFrom = two == transferTo ? one : two;

			Logger.LogTraceFormat("Attempting transfer from {0} into {1}", Category.Chemistry, transferFrom, transferTo);


			if (transferFrom.IsEmpty)
			{
				//red msg
				Chat.AddExamineMsg(performer, "The " + transferFrom.gameObject.ExpensiveName() + " is empty!");
				return;
			}

			var transferAmount = transferFrom.TransferAmount;

			var useFillMessage = true;

			var result = transferFrom.MoveReagentsTo(transferAmount, transferTo);

			string resultMessage;
			if (string.IsNullOrEmpty(result.Message))
				resultMessage = useFillMessage
					? $"You fill the {transferTo.gameObject.ExpensiveName()} with {result.TransferAmount} units of the contents of the {transferFrom.gameObject.ExpensiveName()}."
					: $"You transfer {result.TransferAmount} units of the solution to the {transferTo.gameObject.ExpensiveName()}.";
			else
				resultMessage = result.Message;
			Chat.AddExamineMsg(performer, resultMessage);
		}

		/// <summary>
		/// Moves reagents to another container
		/// </summary>
		public TransferResult MoveReagentsTo(float amount, ReagentContainer target)
		{
			return TransferTo(amount, target);
		}

		/// <summary>
		/// Moves reagents to another container
		/// </summary>
		public TransferResult TransferTo(
			float amount,
			ReagentContainer target
		)
		{
			TransferResult transferResult;

			if (target != null)
			{
				var transffered = CurrentReagentMix.Take(amount);
				transferResult = target.Add(transffered);
				if (!transferResult.Success)
				{
					//don't consume contents if transfer failed
					return transferResult;
				}
			}
			else
			{
				transferResult = new TransferResult
				{
					Success = true,
					TransferAmount = amount,
					Message = "Reagents were consumed"
				};
			}

			return transferResult;
		}

	}
}