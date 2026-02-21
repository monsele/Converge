import { buildModule } from "@nomicfoundation/hardhat-ignition/modules";

/**
 * Ignition module for deploying ConvertibleBondTokenCRE
 * 
 * This module deploys the ConvertibleBondTokenCRE contract with:
 * - A base URI for ERC-1155 token metadata
 * - A Chainlink Forwarder address for CRE integration
 * 
 * Usage:
 *   npx hardhat ignition deploy ignition/modules/ConvertibleBond.ts --network <network>
 * 
 * Networks:
 *   hardhatMainnet - Local Hardhat network
 *   sepolia        - Sepolia testnet
 */
export default buildModule("ConvertibleBondModule", (m) => {
  // Configuration - update these values as needed
  const uri = "https://api.example.com/token/{id}";
  
  // Chainlink Forwarder address
  // For local testing: use "0x0000000000000000000000000000000000000001"
  // For production: replace with actual Chainlink Forwarder contract address
  const chainlinkForwarder = "0x15fC6ae953E024d975e77382eEeC56A9101f9F88";

  // Deploy the ConvertibleBondTokenCRE contract
  const convertibleBond = m.contract("ConvertibleBondTokenCRE", [
    uri,
    chainlinkForwarder,
  ]);

  // Return the deployed contract reference
  return { convertibleBond };
});
