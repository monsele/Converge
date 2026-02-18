import {
  bytesToHex,
  consensusIdenticalAggregation,
  cre,
  getNetwork,
  Runner,
  type Runtime,
} from "@chainlink/cre-sdk";
import { decodeEventLog, parseAbiItem, toEventSelector } from "viem";

interface ListenerConfig {
  network?: string;
  convertibleBondTokenAddress: string;
  apiBaseUrl?: string;
}

interface EvmLogPayload {
  topics: Uint8Array[];
  data: Uint8Array;
  txHash: Uint8Array;
  blockNumber?: {
    absVal: Uint8Array;
    sign: bigint;
  };
  removed?: boolean;
}

interface ApiBondConvertRequest {
  bondId: number;
  equityId: number;
  conversionRatio: string;
  maturityDate: number;
  faceValue: string;
  symbol: string;
  name: string;
  isin: string;
  couponRate: string;
  transactionHash: string;
  blockNumber: number;
  timestamp: number;
}

interface DecodedConvertibleBondArgs {
  bondId: bigint;
  equityId: bigint;
  conversionRatio: bigint;
  maturityDate: bigint;
  faceValue: bigint;
  symbol: string;
  name: string;
  isin: string;
  couponRate: bigint;
}

const CONVERTIBLE_BOND_CREATED_EVENT = parseAbiItem(
  "event ConvertibleBondCreated(uint256 indexed bondId, uint256 indexed equityId, uint256 conversionRatio, uint256 maturityDate, uint256 faceValue, string symbol, string name, string isin, uint256 couponRate)"
);

const CONVERTIBLE_BOND_CREATED_SIGNATURE = toEventSelector(CONVERTIBLE_BOND_CREATED_EVENT);

function creBigIntToNative(value?: { absVal: Uint8Array; sign: bigint }): bigint {
  if (!value) return 0n;

  let result = 0n;
  for (const byte of value.absVal) {
    result = (result << 8n) + BigInt(byte);
  }

  return value.sign < 0n ? -result : result;
}

function toSafeNumber(value: bigint, fieldName: string): number {
  if (value > BigInt(Number.MAX_SAFE_INTEGER)) {
    throw new Error(`${fieldName} exceeds Number.MAX_SAFE_INTEGER`);
  }
  if (value < BigInt(Number.MIN_SAFE_INTEGER)) {
    throw new Error(`${fieldName} is below Number.MIN_SAFE_INTEGER`);
  }
  return Number(value);
}

const onConvertibleBondCreated = (
  runtime: Runtime<ListenerConfig>,
  payload: EvmLogPayload
): string => {
  const apiBaseUrl = runtime.config.apiBaseUrl || "http://localhost:5000";

  if (payload.removed) {
    runtime.log("Skipping removed log from chain reorg.");
    return JSON.stringify({ status: "SkippedRemovedLog" });
  }

  const topics = payload.topics.map((topic) => bytesToHex(topic)) as [
    `0x${string}`,
    ...`0x${string}`[]
  ];
  const data = bytesToHex(payload.data);

  const decoded = decodeEventLog({
    abi: [CONVERTIBLE_BOND_CREATED_EVENT],
    data,
    topics,
    strict: true,
  }) as { eventName: string; args: unknown };

  if (decoded.eventName !== "ConvertibleBondCreated") {
    throw new Error(`Unexpected event name: ${decoded.eventName}`);
  }

  const args = decoded.args as unknown as DecodedConvertibleBondArgs;
  const blockNumber = toSafeNumber(
    creBigIntToNative(payload.blockNumber),
    "blockNumber"
  );

  const requestPayload: ApiBondConvertRequest = {
    bondId: toSafeNumber(args.bondId, "bondId"),
    equityId: toSafeNumber(args.equityId, "equityId"),
    conversionRatio: args.conversionRatio.toString(),
    maturityDate: toSafeNumber(args.maturityDate, "maturityDate"),
    faceValue: args.faceValue.toString(),
    symbol: args.symbol,
    name: args.name,
    isin: args.isin,
    couponRate: args.couponRate.toString(),
    transactionHash: bytesToHex(payload.txHash),
    blockNumber,
    timestamp: Math.floor(Date.now() / 1000),
  };

  runtime.log(
    `ConvertibleBondCreated received. bondId=${requestPayload.bondId}, equityId=${requestPayload.equityId}`
  );

  const statusCode = runtime
    .runInNodeMode(
      (nodeRuntime, url: string, body: string) => {
        const httpClient = new cre.capabilities.HTTPClient();
        const response = httpClient
          .sendRequest(nodeRuntime, {
            url,
            method: "POST",
            headers: {
              "Content-Type": "application/json",
              "X-CRE-Workflow": "ConvertibleBondCreated",
            },
            body,
          })
          .result();

        return response.statusCode;
      },
      consensusIdenticalAggregation<number>()
    )(
      `${apiBaseUrl}/api/createBondConvert`,
      JSON.stringify(requestPayload)
    )
    .result();

  if (statusCode < 200 || statusCode >= 300) {
    throw new Error(`api/createBondConvert returned status ${statusCode}`);
  }

  runtime.log(
    `api/createBondConvert succeeded for bondId=${requestPayload.bondId}`
  );

  return JSON.stringify({
    status: "Success",
    bondId: requestPayload.bondId,
    equityId: requestPayload.equityId,
    txHash: requestPayload.transactionHash,
    blockNumber: requestPayload.blockNumber,
  });
};

const initWorkflow = (config: ListenerConfig) => {
  if (!config.convertibleBondTokenAddress) {
    throw new Error("convertibleBondTokenAddress is required in config");
  }

  const networkName = config.network || "ethereum-testnet-sepolia";
  const isTestnet = !networkName.includes("mainnet");
  const network = getNetwork({
    chainFamily: "evm",
    chainSelectorName: networkName,
    isTestnet,
  });

  if (!network) {
    throw new Error(`Unsupported network: ${networkName}`);
  }

  const evmClient = new cre.capabilities.EVMClient(network.chainSelector.selector);
  const trigger = evmClient.logTrigger({
    addresses: [config.convertibleBondTokenAddress],
    topics: [
      { values: [CONVERTIBLE_BOND_CREATED_SIGNATURE] },
      { values: [] },
      { values: [] },
      { values: [] },
    ],
  });

  return [cre.handler(trigger, onConvertibleBondCreated)];
};

export async function main() {
  const runner = await Runner.newRunner<ListenerConfig>();
  await runner.run(initWorkflow);
}

main();
