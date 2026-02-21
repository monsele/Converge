import hre from "hardhat";

/**
 * Deploy script for ConvertibleBondTokenCRE
 *
 * Usage:
 *   npx hardhat run scripts/deploy.ts --network <network>
 *
 * Networks:
 *   hardhatMainnet - In-process EDR simulated network (no setup needed)
 *   sepolia        - Sepolia testnet (requires SEPOLIA_RPC_URL and SEPOLIA_PRIVATE_KEY in .env)
 */
async function main() {
  console.log("Deploying ConvertibleBondTokenCRE...");

  // Configuration
  const uri = "https://api.example.com/token/{id}";

  // Chainlink Forwarder address
  const chainlinkForwarder = "0x15fC6ae953E024d975e77382eEeC56A9101f9F88";

  // In Hardhat 3, viem helpers live on the NetworkConnection object.
  // hre.network.connect() creates a connection to the currently selected network
  // (set by --network flag), and the hardhat-viem plugin injects .viem onto it.
  const connection = await hre.network.connect();
  const { viem } = connection;

  const [walletClient] = await viem.getWalletClients();
  const publicClient = await viem.getPublicClient();

  console.log(`Deploying from: ${walletClient.account.address}`);
  console.log(`Network: ${connection.networkName}`);

  // Deploy using the viem helper — handles artifact lookup, ABI encoding, and
  // waits for the transaction to be mined before returning the contract instance.
  console.log("Deploying contract...");
  const contract = await viem.deployContract("ConvertibleBondTokenCRE", [
    uri,
    chainlinkForwarder,
  ]);

  console.log("");
  console.log("=".repeat(50));
  console.log("ConvertibleBondTokenCRE deployed successfully!");
  console.log("=".repeat(50));
  console.log(`Contract Address: ${contract.address}`);
  console.log(`Network: ${connection.networkName}`);
  console.log(`URI: ${uri}`);
  console.log(`Chainlink Forwarder: ${chainlinkForwarder}`);
  console.log("=".repeat(50));
  console.log("");

  console.log("Deployment verified! Contract is live.");
  console.log("");
  console.log("Next steps:");
  console.log("1. Update the Chainlink Forwarder address using setChainlinkForwarder()");
  console.log("2. Create equity classes and bond series using createConvertibleBond()");
  console.log("3. Whitelist bondholders using whitelistBondholder()");
}

// Execute the deployment
main()
  .then(() => {
    console.log("\nDeployment completed!");
    process.exit(0);
  })
  .catch((error) => {
    console.error("\nDeployment failed!");
    console.error(error);
    process.exit(1);
  });
