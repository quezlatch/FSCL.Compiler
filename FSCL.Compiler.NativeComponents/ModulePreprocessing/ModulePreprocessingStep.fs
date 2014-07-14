﻿namespace FSCL.Compiler.ModulePreprocessing

open System
open System.Reflection
open System.Collections.Generic
open Microsoft.FSharp.Quotations
open FSCL.Compiler
open FSCL.Compiler.Util.VerboseCompilationUtil

[<assembly:DefaultComponentAssembly>]
do()

[<Step("FSCL_MODULE_PREPROCESSING_STEP", 
      Dependencies = [| "FSCL_MODULE_PARSING_STEP" |])>]
type ModulePreprocessingStep(tm: TypeManager,
                             processors: ICompilerStepProcessor list) = 
    inherit CompilerStep<KernelModule, KernelModule>(tm, processors)
           
    member private this.Process(km, opts) =
        for p in processors do
            p.Execute(km, this, opts) |> ignore
        km

    override this.Run(data, opts) =
        let verb = StartVerboseStep(this, opts)

        let r = ContinueCompilation(this.Process(data, opts))

        StopVerboseStep(verb)
        r

        
