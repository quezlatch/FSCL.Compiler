﻿namespace FSCL.Compiler.AcceleratedCollections

open FSCL.Compiler
open FSCL.Language
open System.Reflection
open System.Reflection.Emit
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Core.LanguagePrimitives
open System.Collections.Generic
open System
open FSCL.Compiler.Util
open Microsoft.FSharp.Reflection
open AcceleratedCollectionUtil
open System.Runtime.InteropServices
open Microsoft.FSharp.Linq.RuntimeHelpers

type AcceleratedArraySumHandler() =
    let cpu_template = 
        <@
            fun(g_idata:int[], g_odata:int[], block: int) ->
                let mutable global_index = get_global_id(0) * block
                let mutable upper_bound = (get_global_id(0) + 1) * block
                if upper_bound > g_idata.Length then
                    upper_bound <- g_idata.Length

                // We don't know which is the neutral value for placeholderComp so we need to
                // initialize it with an element of the input array
                let mutable accumulator = 0
                if global_index < upper_bound then
                    accumulator <- g_idata.[global_index]
                    global_index <- global_index + 1

                while global_index < upper_bound do
                    accumulator <- accumulator + g_idata.[global_index]
                    global_index <- global_index + 1

                g_odata.[get_group_id(0)] <- accumulator
        @>

    // NEW: Two-Stage reduction instead of multi-stage
    let gpu_template = 
        <@
            fun(g_idata:int[], [<Local>]sdata:int[], g_odata:int[]) ->
                let global_index = get_global_id(0)
                let global_size = get_global_size(0)
                let mutable accumulator = g_idata.[global_index]
                for gi in global_index + global_size .. global_size .. g_idata.Length - 1 do
                    accumulator <- accumulator + g_idata.[gi]
                                        
                let local_index = get_local_id(0)
                sdata.[local_index] <- accumulator
                barrier(CLK_LOCAL_MEM_FENCE)

                let mutable offset = get_local_size(0) / 2
                while(offset > 0) do
                    if(local_index < offset) then
                        sdata.[local_index] <- (sdata.[local_index]) + (sdata.[local_index + offset])
                    offset <- offset / 2
                    barrier(CLK_LOCAL_MEM_FENCE)
                
                if local_index = 0 then
                    g_odata.[get_group_id(0)] <- sdata.[0]
        @>
             
    let rec SubstitutePlaceholders(e:Expr, parameters:Dictionary<Var, Var>, accumulatorPlaceholder:Var, actualFunction: MethodInfo) =  
        // Build a call expr
        let RebuildCall(o:Expr option, m: MethodInfo, args:Expr list) =
            if o.IsSome && (not m.IsStatic) then
                Expr.Call(o.Value, m, List.map(fun (e:Expr) -> SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction)) args)
            else
                Expr.Call(m, List.map(fun (e:Expr) -> SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction)) args)  
            
        match e with
        | Patterns.Var(v) ->       
            // Substitute parameter with the new one (of the correct type)
            if v.Name = "accumulator" then
                Expr.Var(accumulatorPlaceholder)
            else if parameters.ContainsKey(v) then
                Expr.Var(parameters.[v])
            else
                e
        | Patterns.Call(o, m, args) ->   
            // If this is the placeholder for the utility function (to be applied to each pari of elements)         
            if m.DeclaringType.Name = "IntrinsicFunctions" then
                match args.[0] with
                | Patterns.Var(v) ->
                    if m.Name = "GetArray" then
                        // Find the placeholder holding the variable
                        if (parameters.ContainsKey(v)) then
                            // Recursively process the arguments, except the array reference
                            let arrayGet, _ = AcceleratedCollectionUtil.GetArrayAccessMethodInfo(parameters.[v].Type.GetElementType())
                            Expr.Call(arrayGet, [ Expr.Var(parameters.[v]); SubstitutePlaceholders(args.[1], parameters, accumulatorPlaceholder, actualFunction) ])
                        else
                            RebuildCall(o, m, args)
                    else if m.Name = "SetArray" then
                        // Find the placeholder holding the variable
                        if (parameters.ContainsKey(v)) then
                            // Recursively process the arguments, except the array reference)
                            let _, arraySet = AcceleratedCollectionUtil.GetArrayAccessMethodInfo(parameters.[v].Type.GetElementType())
                            // If the value is const (e.g. 0) then it must be converted to the new array element type
                            let newValue = match args.[2] with
                                            | Patterns.Value(o, t) ->
                                                let outputParameterType = actualFunction.GetParameters().[1].ParameterType
                                                // Conversion method (ToDouble, ToSingle, ToInt, ...)
                                                Expr.Value(Activator.CreateInstance(outputParameterType), outputParameterType)
                                            | _ ->
                                                SubstitutePlaceholders(args.[2], parameters, accumulatorPlaceholder, actualFunction)
                            Expr.Call(arraySet, [ Expr.Var(parameters.[v]); SubstitutePlaceholders(args.[1], parameters, accumulatorPlaceholder, actualFunction); newValue ])
                                                           
                        else
                            RebuildCall(o, m, args)
                    else
                         RebuildCall(o, m,args)
                | _ ->
                    RebuildCall(o, m, List.map(fun (e:Expr) -> SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction)) args)                  
            // Otherwise process children and return the same call
            else
                RebuildCall(o, m, List.map(fun (e:Expr) -> SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction)) args)
        | Patterns.Let(v, value, body) ->
            if v.Name = "accumulator" then
                Expr.Let(accumulatorPlaceholder, Expr.Coerce(SubstitutePlaceholders(value, parameters, accumulatorPlaceholder, actualFunction), accumulatorPlaceholder.Type), SubstitutePlaceholders(body, parameters, accumulatorPlaceholder, actualFunction))
            // a and b are "special" vars that hold the params of the reduce function
            else if v.Name = "a" then
                let a = Quotations.Var("a", actualFunction.GetParameters().[0].ParameterType, false)
                parameters.Add(v, a)
                Expr.Let(a, SubstitutePlaceholders(value, parameters, accumulatorPlaceholder, actualFunction), 
                            SubstitutePlaceholders(body, parameters, accumulatorPlaceholder, actualFunction))            
            else if v.Name = "b" then
                let b = Quotations.Var("b", actualFunction.GetParameters().[1].ParameterType, false)
                // Remember for successive references to a and b
                parameters.Add(v, b)
                Expr.Let(b, SubstitutePlaceholders(value, parameters, accumulatorPlaceholder, actualFunction), SubstitutePlaceholders(body, parameters, accumulatorPlaceholder, actualFunction))
            else
                Expr.Let(v, SubstitutePlaceholders(value, parameters, accumulatorPlaceholder, actualFunction), SubstitutePlaceholders(body, parameters, accumulatorPlaceholder, actualFunction))
        | ExprShape.ShapeLambda(v, b) ->
            Expr.Lambda(v, SubstitutePlaceholders(b, parameters, accumulatorPlaceholder, actualFunction))                    
        | ExprShape.ShapeCombination(o, l) ->
            match e with
            | Patterns.IfThenElse(cond, ifb, elseb) ->
                let nl = new List<Expr>();
                for e in l do 
                    let ne = SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction) 
                    // Trick to adapt "0" in (sdata.[tid] <- if(i < n) then g_idata.[i] else 0) in case of other type of values (double: 0.0)
                    nl.Add(ne)
                ExprShape.RebuildShapeCombination(o, List.ofSeq(nl))
            | _ ->
                let nl = new List<Expr>();
                for e in l do 
                    let ne = SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction) 
                    nl.Add(ne)
                ExprShape.RebuildShapeCombination(o, List.ofSeq(nl))
        | _ ->
            e

    member this.EvaluateAndApply(e:Expr) (a:obj) (b:obj) =
        let f = LeafExpressionConverter.EvaluateQuotation(e)
        let fm = f.GetType().GetMethod("Invoke")
        let r1 = fm.Invoke(f, [| a |])
        let r2m = r1.GetType().GetMethod("Invoke")
        let r2 = r2m.Invoke(r1, [| b |])
        r2

    interface IAcceleratedCollectionHandler with
        member this.Process(methodInfo, cleanArgs, root, meta, step) =       
            (*
                Array map looks like: Array.map fun collection
                At first we check if fun is a lambda (first argument)
                and in this case we transform it into a method
                Secondly, we iterate parsing on the second argument (collection)
                since it might be a subkernel
            *)
            let lambda, computationFunction =                
                AcceleratedCollectionUtil.ExtractComputationFunction(cleanArgs, root)
                                
            // Extract the reduce function 
            match computationFunction with
            | Some(functionInfo, functionParamVars, body) ->
                // Create on-the-fly module to host the kernel                
                // The dynamic module that hosts the generated kernels
                let assemblyName = IDGenerator.GenerateUniqueID("FSCL.Compiler.Plugins.AcceleratedCollections.AcceleratedArray");
                let assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
                let moduleBuilder = assemblyBuilder.DefineDynamicModule("AcceleratedArrayModule");

                // Now create the kernel
                // We need to get the type of a array whose elements type is the same of the functionInfo parameter
                let inputArrayType = Array.CreateInstance(functionInfo.GetParameters().[0].ParameterType, 0).GetType()
                let outputArrayType = Array.CreateInstance(functionInfo.ReturnType, 0).GetType()
                // Now that we have the types of the input and output arrays, create placeholders (var) for the kernel input and output       
                
                // Check device target
                let targetType = meta.KernelMeta.Get<DeviceTypeAttribute>()
            
                let kModule = 
                    // GPU CODE
                    match targetType.Type with
                    | DeviceType.Gpu ->                    
                        // Now we can create the signature and define parameter name in the dynamic module                                                
                        let signature = DynamicMethod("ArrayReduce_" + functionInfo.Name, outputArrayType, [| inputArrayType; outputArrayType; outputArrayType |])
                        signature.DefineParameter(1, ParameterAttributes.In, "input_array") |> ignore
                        signature.DefineParameter(2, ParameterAttributes.In, "local_array") |> ignore
                        signature.DefineParameter(3, ParameterAttributes.In, "output_array") |> ignore
                        
                        // Create parameters placeholders
                        let inputHolder = Quotations.Var("input_array", inputArrayType)
                        let localHolder = Quotations.Var("local_array", outputArrayType)
                        let outputHolder = Quotations.Var("output_array", outputArrayType)
                        let accumulatorPlaceholder = Quotations.Var("accumulator", outputArrayType.GetElementType())
                        let tupleHolder = Quotations.Var("tupledArg", FSharpType.MakeTupleType([| inputHolder.Type; localHolder.Type; outputHolder.Type |]))

                        // Finally, create the body of the kernel
                        let templateBody, templateParameters = AcceleratedCollectionUtil.GetKernelFromLambda(gpu_template)   
                        let parameterMatching = new Dictionary<Var, Var>()
                        parameterMatching.Add(templateParameters.[0], inputHolder)
                        parameterMatching.Add(templateParameters.[1], localHolder)
                        parameterMatching.Add(templateParameters.[2], outputHolder)

                        // Replace functions and references to parameters
                        let functionMatching = new Dictionary<string, MethodInfo>()
                        let newBody = SubstitutePlaceholders(templateBody, parameterMatching, accumulatorPlaceholder, functionInfo)  
                        let finalKernel = 
                            Expr.Lambda(tupleHolder,
                                Expr.Let(inputHolder, Expr.TupleGet(Expr.Var(tupleHolder), 0),
                                    Expr.Let(localHolder, Expr.TupleGet(Expr.Var(tupleHolder), 1),
                                        Expr.Let(outputHolder, Expr.TupleGet(Expr.Var(tupleHolder), 2),
                                            newBody))))

                        let kInfo = new AcceleratedKernelInfo(signature, 
                                                              [ inputHolder; localHolder; outputHolder ],
                                                              finalKernel, 
                                                              meta, 
                                                              "Array.reduce", body)
                        let kernelModule = new KernelModule(kInfo, cleanArgs)
                        
                        kernelModule                
                    |_ ->
                        // CPU CODE                    
                        // Now we can create the signature and define parameter name in the dynamic module                                        
                        let signature = DynamicMethod("ArrayReduce_" + functionInfo.Name, outputArrayType, [| inputArrayType; outputArrayType; typeof<int> |])
                        signature.DefineParameter(1, ParameterAttributes.In, "input_array") |> ignore
                        signature.DefineParameter(2, ParameterAttributes.In, "output_array") |> ignore
                        signature.DefineParameter(3, ParameterAttributes.In, "block") |> ignore
                    
                        // Create parameters placeholders
                        let inputHolder = Quotations.Var("input_array", inputArrayType)
                        let blockHolder = Quotations.Var("block", typeof<int>)
                        let outputHolder = Quotations.Var("output_array", outputArrayType)
                        let accumulatorPlaceholder = Quotations.Var("accumulator", outputArrayType.GetElementType())
                        let tupleHolder = Quotations.Var("tupledArg", FSharpType.MakeTupleType([| inputHolder.Type; outputHolder.Type; blockHolder.Type |]))

                        // Finally, create the body of the kernel
                        let templateBody, templateParameters = AcceleratedCollectionUtil.GetKernelFromLambda(cpu_template)   
                        let parameterMatching = new Dictionary<Var, Var>()
                        parameterMatching.Add(templateParameters.[0], inputHolder)
                        parameterMatching.Add(templateParameters.[1], outputHolder)
                        parameterMatching.Add(templateParameters.[2], blockHolder)

                        // Replace functions and references to parameters
                        let functionMatching = new Dictionary<string, MethodInfo>()
                        let newBody = SubstitutePlaceholders(templateBody, parameterMatching, accumulatorPlaceholder, functionInfo)  
                        let finalKernel = 
                            Expr.Lambda(tupleHolder,
                                Expr.Let(inputHolder, Expr.TupleGet(Expr.Var(tupleHolder), 0),
                                    Expr.Let(outputHolder, Expr.TupleGet(Expr.Var(tupleHolder), 1),
                                        Expr.Let(blockHolder, Expr.TupleGet(Expr.Var(tupleHolder), 2),
                                            newBody))))
                    
                        // Setup kernel module and return
                        let kInfo = new AcceleratedKernelInfo(signature, 
                                                              [ inputHolder; outputHolder; blockHolder ],
                                                              finalKernel, 
                                                              meta, 
                                                              "Array.reduce", body)
                        let kernelModule = new KernelModule(kInfo, cleanArgs)
                        
                        kernelModule 

                // Add applied function                                 
                let reduceFunctionInfo = new FunctionInfo(functionInfo, functionParamVars, body, lambda.IsSome)
                
                // Store the called function (runtime execution will use it to perform latest iterations of reduction)
                if lambda.IsSome then
                    kModule.Kernel.CustomInfo.Add("ReduceFunction", lambda.Value)
                else
                    kModule.Kernel.CustomInfo.Add("ReduceFunction", match computationFunction.Value with a, _, _ -> a)
                                    
                // Store the called function (runtime execution will use it to perform latest iterations of reduction)
                kModule.Functions.Add(reduceFunctionInfo.ID, reduceFunctionInfo)
                // Return module                             
                Some(kModule)

            | _ ->
                None