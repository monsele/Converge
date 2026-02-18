import { cre, Runner, type Runtime, type HTTPPayload, decodeJson, getNetwork, hexToBase64, TxStatus, bytesToHex } from "@chainlink/cre-sdk";
import { encodeAbiParameters, parseAbiParameters } from "viem";

// Define the shape of the data coming from C#
interface BondRequest {
  issuerId: string;
  isin: string;
  currency: string;
  totalSize: string; // BigInt passed as string
  faceValue: string;
  maturityDate: number;
  conversionRatio: number;
  conversionPrice: number;
}

// ABI encoding matching the Solidity struct
const BOND_PARAMS = parseAbiParameters(
  "string issuerId, string isin, string currency, uint256 totalSize, uint256 faceValue, uint256 maturity, uint256 convRatio, uint256 convPrice"
);

const onBondIssuanceTrigger = (runtime: Runtime, payload: HTTPPayload): string => {
  runtime.log("Received Bond Issuance Request from C# API");

  // 1. Decode JSON from C#
  const input = decodeJson(payload.input) as BondRequest;
  
  // 2. Configure EVM Client (Sepolia Testnet for this example)
  const network = getNetwork({ chainFamily: "evm", chainSelectorName: "ethereum-testnet-sepolia", isTestnet: true });
  const evmClient = new cre.capabilities.EVMClient(network.chainSelector.selector);
  const contractAddress = runtime.config.bondMasterAddress; // Defined in config.json

  // 3. Encode Data for Solidity
  const reportData = encodeAbiParameters(BOND_PARAMS, [
    input.issuerId,
    input.isin,
    input.currency,
    BigInt(input.totalSize),
    BigInt(input.faceValue),
    BigInt(input.maturityDate),
    BigInt(input.conversionRatio),
    BigInt(input.conversionPrice)
  ]);

  // 4. Generate Signed Report (2-step pattern)
  const reportResponse = runtime.report({
    encodedPayload: hexToBase64(reportData),
    encoderName: "evm",
    signingAlgo: "ecdsa",
    hashingAlgo: "keccak256",
  }).result();

  // 5. Write to Blockchain
  const writeResult = evmClient.writeReport(runtime, {
    receiver: contractAddress,
    report: reportResponse,
    gasConfig: { gasLimit: "1000000" } // Adjust gas as needed
  }).result();

  if (writeResult.txStatus === TxStatus.SUCCESS) {
    const txHash = bytesToHex(writeResult.txHash || new Uint8Array(32));
    runtime.log(`Bond Master Updated! Tx Hash: ${txHash}`);
    return JSON.stringify({ status: "Success", txHash: txHash });
  }

  throw new Error(`Transaction failed: ${writeResult.txStatus}`);
};

// Initialize Workflow with HTTP Trigger
const initWorkflow = (config: any) => {
  const httpCapability = new cre.capabilities.HTTPCapability();
  // In production, add authorizedKeys here to secure the webhook
  const trigger = httpCapability.trigger({}); 
  
  return [cre.handler(trigger, onBondIssuanceTrigger)];
};

export async function main() {
  const runner = await Runner.newRunner();
  await runner.run(initWorkflow);
}
main();