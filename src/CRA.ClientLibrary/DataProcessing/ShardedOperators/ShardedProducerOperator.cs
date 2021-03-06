﻿using System;
using System.Reflection;
using System.Linq.Expressions;
using System.Threading.Tasks;
using System.Collections.Generic;
using CRA.DataProvider;
using Remote.Linq;
using Remote.Linq.ExpressionVisitors;
using System.Threading;

namespace CRA.ClientLibrary.DataProcessing
{
    public class ShardedProducerOperator : ShardedOperatorBase
    {
        internal Type _outputKeyType;
        internal Type _outputPayloadType;
        internal Type _outputDatasetType;
        internal string _outputId;

        internal CountdownEvent _deployProduceOutput;
        internal CountdownEvent _runProduceOutput;

        internal CountdownEvent _deployProduceInput;
        internal CountdownEvent _runProduceInput;

        internal CountdownEvent _continueAfterTransformation;

        internal Dictionary<string, CountdownEvent> _startCreatingSecondaryDatasets;
        internal Dictionary<string, CountdownEvent> _finishCreatingSecondaryDatasets;

        internal Dictionary<string, BinaryOperatorTypes> _binaryOperatorTypes;

        internal bool _isTransformationsApplied = false;
        internal object _transformationLock = new object();

        public ShardedProducerOperator() : base()
        {
            _startCreatingSecondaryDatasets = new Dictionary<string, CountdownEvent>();
            _finishCreatingSecondaryDatasets = new Dictionary<string, CountdownEvent>();

            _binaryOperatorTypes = new Dictionary<string, BinaryOperatorTypes>();
        }

        internal override void InitializeOperator(int shardId, ShardingInfo shardingInfo)
        {
            _hasSplittedOutput = HasSplittedOutput();
            string[] toEndpoints = GetEndpointNamesForVertex(VertexName.Split('$')[0], _toFromConnections);
            string[] fromEndpoints = GetEndpointNamesForVertex(VertexName.Split('$')[0], _fromToConnections);

            int secondaryOutputsCount = 0;
            int ordinaryOutputSCount = 0;
            foreach (var fromEndpoint in fromEndpoints)
            {
                var toTuple = _fromToConnections[new Tuple<string, string>(VertexName.Split('$')[0], fromEndpoint)];
                if (toTuple.Item4)
                    secondaryOutputsCount++;
                else
                    ordinaryOutputSCount++;
            }
            int deployProduceInputCount = secondaryOutputsCount;
            if (_hasSplittedOutput)
                deployProduceInputCount += shardingInfo.AllShards.Length;
            else
                deployProduceInputCount += ordinaryOutputSCount;
            _deployProduceInput = new CountdownEvent(deployProduceInputCount);
            _runProduceInput = new CountdownEvent(deployProduceInputCount);

            int secondaryInputsCount = 0;
            foreach (var toEndpoint in toEndpoints)
            {
                var fromTuple = _toFromConnections[new Tuple<string, string>(VertexName.Split('$')[0], toEndpoint)];
                if (fromTuple.Item4) secondaryInputsCount++;
            }
            _deployProduceOutput = new CountdownEvent(secondaryInputsCount);
            _runProduceOutput = new CountdownEvent(secondaryInputsCount);

            _continueAfterTransformation = new CountdownEvent(1);

            if (secondaryInputsCount > 0) _hasSecondaryInput = true;

            foreach (var toEndpoint in toEndpoints)
            {
                var fromTuple = _toFromConnections[new Tuple<string, string>(VertexName.Split('$')[0], toEndpoint)];
                if (!fromTuple.Item4)
                    throw new NotImplementedException("Shared input endpoints are not supported in produce operators!!");
                else
                {
                    _startCreatingSecondaryDatasets[fromTuple.Item1] = new CountdownEvent(1);
                    _finishCreatingSecondaryDatasets[fromTuple.Item1] = new CountdownEvent(1);
                    AddAsyncInputEndpoint(toEndpoint, new ShardedProducerSecondaryInput(this, shardId, shardingInfo.AllShards.Length, toEndpoint));
                }
            }

            if (!_hasSecondaryInput)
            {
                CreateAndTransformDataset(shardId);
                _isTransformationsApplied = true;
                _continueAfterTransformation.Signal();
            }

            foreach (var fromEndpoint in fromEndpoints)
            {
                var toTuple = _fromToConnections[new Tuple<string, string>(VertexName.Split('$')[0], fromEndpoint)];
                if (!toTuple.Item4)
                    AddAsyncOutputEndpoint(fromEndpoint, new ShardedProducerOutput(this, shardId, shardingInfo.AllShards.Length, fromEndpoint));
                else
                    AddAsyncOutputEndpoint(fromEndpoint, new ShardedProducerSecondaryOutput(this, shardId, shardingInfo.AllShards.Length, fromEndpoint));
            }
        }
        
        public void CreateAndTransformDataset(int shardId)
        {
            var produceTask = (ProduceTask)_task;

            MethodInfo method = typeof(ShardedProducerOperator).GetMethod("CreateDatasetFromExpression");
            MethodInfo generic = method.MakeGenericMethod(
                                    new Type[] {produceTask.OperationTypes.OutputKeyType,
                                                produceTask.OperationTypes.OutputPayloadType,
                                                produceTask.OperationTypes.OutputDatasetType});
            object[] arguments = new Object[] { shardId, produceTask.DataProducer };
            _cachedDatasets[shardId][produceTask.OutputId] = generic.Invoke(this, arguments);

            _outputKeyType = produceTask.OperationTypes.OutputKeyType;
            _outputPayloadType = produceTask.OperationTypes.OutputPayloadType;
            _outputDatasetType = produceTask.OperationTypes.OutputDatasetType;
            _outputId = produceTask.OutputId;

            if (_task.Transforms != null)
            {
                for (int i = 0; i < _task.Transforms.Length; i++)
                {
                    object dataset1 = null; string dataset1Id = null;
                    object dataset2 = null; string dataset2Id = null;
                    TransformUtils.PrepareTransformInputs(_task.TransformsInputs[i], ref dataset1, ref dataset1Id,
                                        ref dataset2, ref dataset2Id, _cachedDatasets[shardId]);

                    string transformType = _task.TransformsOperations[i];
                    object transformOutput = null;
                    if (transformType == OperatorType.UnaryTransform.ToString())
                    {
                        UnaryOperatorTypes unaryTransformTypes = new UnaryOperatorTypes();
                        unaryTransformTypes.FromString(_task.TransformsTypes[i]);
                        if (dataset1Id == "$" && dataset1 == null)
                            throw new InvalidOperationException();

                        method = typeof(TransformUtils).GetMethod("ApplyUnaryTransformer");
                        generic = method.MakeGenericMethod(
                              new Type[] { unaryTransformTypes.InputKeyType, unaryTransformTypes.InputPayloadType,
                                       unaryTransformTypes.InputDatasetType, unaryTransformTypes.OutputKeyType,
                                       unaryTransformTypes.OutputPayloadType, unaryTransformTypes.OutputDatasetType
                              });
                        arguments = new Object[] { dataset1, _task.Transforms[i] };

                        _outputKeyType = unaryTransformTypes.OutputKeyType;
                        _outputPayloadType = unaryTransformTypes.OutputPayloadType;
                        _outputDatasetType = unaryTransformTypes.OutputDatasetType;
                    }
                    else if (transformType == OperatorType.BinaryTransform.ToString())
                    {
                        BinaryOperatorTypes binaryTransformTypes = new BinaryOperatorTypes();
                        binaryTransformTypes.FromString(_task.TransformsTypes[i]);
                        if (dataset1Id == "$" && dataset1 == null)
                                      throw new InvalidOperationException();
                        if (dataset2Id == "$" && dataset2 == null)
                        {
                            dataset2Id = _task.TransformsInputs[i].InputId2;
                            _binaryOperatorTypes[dataset2Id] = binaryTransformTypes;

                            _startCreatingSecondaryDatasets[dataset2Id].Signal();
                            _finishCreatingSecondaryDatasets[dataset2Id].Wait();

                            dataset2 = _cachedDatasets[shardId][dataset2Id];
                        }

                        method = typeof(TransformUtils).GetMethod("ApplyBinaryTransformer");
                        generic = method.MakeGenericMethod(
                            new Type[] {binaryTransformTypes.InputKeyType, binaryTransformTypes.InputPayloadType,
                                binaryTransformTypes.InputDatasetType, binaryTransformTypes.SecondaryKeyType,
                                binaryTransformTypes.SecondaryPayloadType, binaryTransformTypes.SecondaryDatasetType,
                                binaryTransformTypes.OutputKeyType, binaryTransformTypes.OutputPayloadType,
                                binaryTransformTypes.OutputDatasetType
                            });
                        arguments = new Object[] { dataset1, dataset2, _task.Transforms[i] };

                        _outputKeyType = binaryTransformTypes.OutputKeyType;
                        _outputPayloadType = binaryTransformTypes.OutputPayloadType;
                        _outputDatasetType = binaryTransformTypes.OutputDatasetType;

                    }
                    else if (transformType == OperatorType.MoveSplit.ToString())
                    {
                        BinaryOperatorTypes splitTypes = new BinaryOperatorTypes();
                        splitTypes.FromString(_task.TransformsTypes[i]);
                        if (dataset1Id == "$" && dataset1 == null)
                            throw new InvalidOperationException();

                        method = typeof(MoveUtils).GetMethod("ApplySplitter");
                        generic = method.MakeGenericMethod(
                            new Type[] {splitTypes.InputKeyType, splitTypes.InputPayloadType,
                                    splitTypes.InputDatasetType, splitTypes.SecondaryKeyType,
                                    splitTypes.SecondaryPayloadType, splitTypes.SecondaryDatasetType
                            });
                        arguments = new Object[] { dataset1, _task.SecondaryShuffleDescriptor, _task.Transforms[i] };

                        _outputKeyType = splitTypes.SecondaryKeyType;
                        _outputPayloadType = splitTypes.SecondaryPayloadType;
                        _outputDatasetType = splitTypes.SecondaryDatasetType;
                    }
                    else
                        throw new InvalidOperationException("Error: Unsupported transformation type");

                    transformOutput = generic.Invoke(this, arguments);
                    if (transformOutput != null)
                    {
                        if (!_cachedDatasets[shardId].ContainsKey(dataset1Id))
                            _cachedDatasets[shardId].Add(dataset1Id, transformOutput);
                        else
                            _cachedDatasets[shardId][dataset1Id] = transformOutput;
                    }

                    _outputId = dataset1Id;
                }
            }
        }
        
        public object CreateDatasetFromExpression<TKey, TPayload, TDataset>(int shardId, string producerExpression)
            where TDataset : IDataset<TKey, TPayload>
        {
            Expression producer = SerializationHelper.Deserialize(producerExpression);
            var compiledProducer = Expression<Func<int, TDataset>>.Lambda(producer).Compile();
            Delegate compiledProducerConstructor = (Delegate)compiledProducer.DynamicInvoke();
            return compiledProducerConstructor.DynamicInvoke(shardId);
        }

        internal override bool HasSplittedOutput()
        {
            if (_task.Transforms != null && _task.Transforms.Length != 0 &&
                _task.TransformsOperations[_task.Transforms.Length - 1] == OperatorType.MoveSplit.ToString())
                return true;
            else
                return false;
        }
    }
}
