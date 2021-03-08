﻿namespace EFT.Trainer.Extensions
{
	public static class PlayerExtensions
	{
		public static bool IsValid(this Player player)
		{
			return player != null 
			       && player.Transform != null 
			       && player.PlayerBones != null 
			       && player.PlayerBones.transform != null;
		}

		public static bool IsAlive(this Player player)
		{
			if (!IsValid(player))
				return false;

			return player.HealthController != null && player.HealthController.IsAlive;
		}
	}
}
