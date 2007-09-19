// 
// (C) 2006-2007 The SharpOS Project Team (http://www.sharpos.org)
//
// Authors:
//	Mircea-Cristian Racasan <darx_kies@gmx.net>
//	Bruce <illuminus86@gmail.com>
//
// Licensed under the terms of the GNU GPL License version 2.
//

using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using SharpOS.AOT.IR;
using SharpOS.AOT.IR.Instructions;
using SharpOS.AOT.IR.Operands;
using SharpOS.AOT.IR.Operators;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Metadata;

namespace SharpOS.AOT.X86 {
	internal class AssemblyMethod {
		/// <summary>
		/// Initializes a new instance of the <see cref="AssemblyMethod"/> class.
		/// </summary>
		/// <param name="assembly">The assembly.</param>
		/// <param name="method">The method.</param>
		public AssemblyMethod (Assembly assembly, Method method)
		{
			this.assembly = assembly;
			this.method = method;
		}

		protected Method method = null;
		protected Assembly assembly = null;

		/// <summary>
		/// Gets the assembly code.
		/// </summary>
		/// <returns></returns>
		public bool GetAssemblyCode ()
		{
			string fullname = method.MethodFullName;

			foreach (CustomAttribute attribute in method.MethodDefinition.CustomAttributes) {
				if (attribute.Constructor.DeclaringType.FullName.Equals (typeof (SharpOS.AOT.Attributes.LabelAttribute).ToString ())) {
					assembly.LABEL (attribute.ConstructorParameters [0].ToString ());

					this.assembly.AddSymbol (new COFF.Label (attribute.ConstructorParameters [0].ToString ()));

				} else if (attribute.Constructor.DeclaringType.FullName.Equals (typeof (SharpOS.AOT.Attributes.KernelMainAttribute).ToString ())) {
					assembly.LABEL (Assembly.KERNEL_MAIN);

					this.assembly.AddSymbol (new COFF.Label (Assembly.KERNEL_MAIN));
				}
			}

			this.assembly.AddSymbol (new COFF.Function (fullname));

			assembly.LABEL (fullname);
			assembly.PUSH (R32.EBP);
			assembly.MOV (R32.EBP, R32.ESP);
			assembly.PUSH (R32.EBX);
			assembly.PUSH (R32.ESI);
			assembly.PUSH (R32.EDI);

			if (method.StackSize > 0)
				assembly.SUB (R32.ESP, (UInt32) (method.StackSize * 4));

			foreach (Block block in method) {

				assembly.LABEL (fullname + " " + block.Index.ToString());

				foreach (SharpOS.AOT.IR.Instructions.Instruction instruction in block) {
					assembly.COMMENT (instruction.ToString ());

					if (instruction is SharpOS.AOT.IR.Instructions.Call
							&& Assembly.IsAssemblyStub ((instruction as SharpOS.AOT.IR.Instructions.Call).Method.Method.DeclaringType.FullName)) {
						this.HandleAssemblyStub (block, instruction);

					} else if (instruction is SharpOS.AOT.IR.Instructions.Call) {
						this.HandleCall (block, (instruction as SharpOS.AOT.IR.Instructions.Call).Method as SharpOS.AOT.IR.Operands.Call);

					} else if (instruction is SharpOS.AOT.IR.Instructions.Assign) {
						this.HandleAssign (block, instruction);

					} else if (instruction is SharpOS.AOT.IR.Instructions.Switch) {
						this.HandleSwitch (block, instruction);

					} else if (instruction is SharpOS.AOT.IR.Instructions.ConditionalJump) {
						this.HandleConditionalJump (block, instruction);

					} else if (instruction is SharpOS.AOT.IR.Instructions.Jump) {
						this.HandleJump (block, instruction);

					} else if (instruction is SharpOS.AOT.IR.Instructions.Return) {
						this.HandleReturn (block, instruction as SharpOS.AOT.IR.Instructions.Return);

					} else if (instruction is SharpOS.AOT.IR.Instructions.Pop) {
						// Nothing to do

					} else if (instruction is SharpOS.AOT.IR.Instructions.System) {
						this.HandlerSystem (block, instruction as SharpOS.AOT.IR.Instructions.System);

					} else
						throw new Exception ("'" + instruction + "' is not supported.");
				}
			}

			assembly.LABEL (fullname + " exit");

			assembly.LEA (R32.ESP, new DWordMemory (null, R32.EBP, null, 0, -12));
			assembly.POP (R32.EDI);
			assembly.POP (R32.ESI);
			assembly.POP (R32.EBX);
			assembly.POP (R32.EBP);
			assembly.RET();

			return true;
		}

		private void HandleSwitch (Block block, SharpOS.AOT.IR.Instructions.Instruction instruction)
		{
			SharpOS.AOT.IR.Instructions.Switch _switch = instruction as SharpOS.AOT.IR.Instructions.Switch;

			this.MovRegisterOperand (R32.EAX, _switch.Value);

			// The first block (0) is the one that is used to bail out if the switch tests are all false.
			for (byte i = 1; i < block.Outs.Count; i++) {
				string label = method.MethodFullName + " " + block.Outs [i].Index.ToString ();
				byte _case = (byte) (i - 1);

				assembly.CMP (R32.EAX, _case);
				assembly.JE (label);
			}

			assembly.JMP (method.MethodFullName + " " + block.Outs [0].Index.ToString ());

			return;
		}

		/// <summary>
		/// Handles the conditional jump.
		/// </summary>
		/// <param name="block">The block.</param>
		/// <param name="instruction">The instruction.</param>
		private void HandleConditionalJump (Block block, SharpOS.AOT.IR.Instructions.Instruction instruction)
		{
			SharpOS.AOT.IR.Instructions.ConditionalJump jump = instruction as SharpOS.AOT.IR.Instructions.ConditionalJump;

			string label = method.MethodFullName + " " + block.Outs[0].Index.ToString(); //.StartOffset.ToString();

			if (jump.Value is SharpOS.AOT.IR.Operands.Boolean) {
				SharpOS.AOT.IR.Operands.Boolean expression = jump.Value as SharpOS.AOT.IR.Operands.Boolean;

				if (expression.Operator is SharpOS.AOT.IR.Operators.Relational) {
					if (IsFourBytes (expression.Operands[0])
							&& IsFourBytes (expression.Operands[1])) {
						R32Type spare1 = assembly.GetSpareRegister();
						R32Type spare2 = assembly.GetSpareRegister();

						this.MovRegisterOperand (spare1, expression.Operands[0]);
						this.MovRegisterOperand (spare2, expression.Operands[1]);

						assembly.CMP (spare1, spare2);

						assembly.FreeSpareRegister (spare1);
						assembly.FreeSpareRegister (spare2);

						SharpOS.AOT.IR.Operators.Relational relational = expression.Operator as SharpOS.AOT.IR.Operators.Relational;

						switch (relational.Type) {

							case Operator.RelationalType.Equal:
								assembly.JE (label);

								break;

							case Operator.RelationalType.NotEqualOrUnordered:
								assembly.JNE (label);

								break;

							case Operator.RelationalType.LessThan:
								assembly.JL (label);

								break;

							case Operator.RelationalType.LessThanOrEqual:
								assembly.JLE (label);

								break;

							case Operator.RelationalType.GreaterThan:
								assembly.JG (label);

								break;

							case Operator.RelationalType.GreaterThanOrEqual:
								assembly.JGE (label);

								break;

							case Operator.RelationalType.LessThanUnsignedOrUnordered:
								assembly.JB (label);

								break;

							case Operator.RelationalType.LessThanOrEqualUnsignedOrUnordered:
								assembly.JBE (label);

								break;

							case Operator.RelationalType.GreaterThanUnsignedOrUnordered:
								assembly.JA (label);

								break;

							case Operator.RelationalType.GreaterThanOrEqualUnsignedOrUnordered:
								assembly.JAE (label);

								break;

							default:
								throw new Exception ("'" + relational.Type + "' is not supported.");
						}

					} else {
						SharpOS.AOT.IR.Operators.Boolean.RelationalType type = (expression.Operator as SharpOS.AOT.IR.Operators.Relational).Type;
						string errorLabel = assembly.GetCMPLabel;

						this.CMP (type, expression.Operands[0], expression.Operands[1], label, errorLabel, errorLabel);

						assembly.LABEL (errorLabel);
					}

				} else if (expression.Operator is SharpOS.AOT.IR.Operators.Boolean) {
					// TODO i4, i8, r4, r8 check?

					SharpOS.AOT.IR.Operators.Boolean boolean = expression.Operator as SharpOS.AOT.IR.Operators.Boolean;

					if (expression.Operands[0].IsRegisterSet) {
						R32Type register = Assembly.GetRegister (expression.Operands[0].Register);

						assembly.TEST (register, register);

					} else {
						R32Type register = assembly.GetSpareRegister();

						if (expression.Operands [0] is Constant)
							this.MovRegisterConstant (register, expression.Operands [0] as Constant);

						else if (expression.Operands [0] is Identifier)
							this.MovRegisterMemory (register, expression.Operands[0] as Identifier);

						else
							throw new Exception ("'" + expression.Operands[0] + "' is not supported.");

						assembly.TEST (register, register);

						assembly.FreeSpareRegister (register);
					}

					switch (boolean.Type) {

						case Operator.BooleanType.True:
							assembly.JNE (label);

							break;

						case Operator.BooleanType.False:
							assembly.JE (label);

							break;

						default:
							throw new Exception ("'" + expression.Operator.GetType() + "' is not supported.");
					}

				} else {
					throw new Exception ("'" + expression.Operator.GetType() + "' is not supported.");
				}

			} else {
				throw new Exception ("'" + jump.Value.GetType() + "' is not supported.");
			}
		}

		/// <summary>
		/// Handles the jump.
		/// </summary>
		/// <param name="block">The block.</param>
		/// <param name="instruction">The instruction.</param>
		private void HandleJump (Block block, SharpOS.AOT.IR.Instructions.Instruction instruction)
		{
			SharpOS.AOT.IR.Instructions.Jump jump = instruction as SharpOS.AOT.IR.Instructions.Jump;

			assembly.JMP (method.MethodFullName + " " + block.Outs[0].Index.ToString()); //.StartOffset.ToString());
		}

		/// <summary>
		/// This handles the Asm.XXX calls.
		/// </summary>
		/// <param name="block">The block.</param>
		/// <param name="instruction">The instruction.</param>
		private void HandleAssemblyStub (Block block, SharpOS.AOT.IR.Instructions.Instruction instruction)
		{
			SharpOS.AOT.IR.Instructions.Call call = instruction as SharpOS.AOT.IR.Instructions.Call;

			string parameterTypes = string.Empty;
			object[] operands = new object[call.Method.Method.Parameters.Count];

			for (int i = 0; i < call.Method.Method.Parameters.Count; i++) {
				ParameterDefinition parameter = call.Method.Method.Parameters[i];

				if (parameterTypes.Length > 0) {
					parameterTypes += " ";
				}

				if (call.Value.Operands[i] is SharpOS.AOT.IR.Operands.Indirect) {
					Operand operand = (call.Value.Operands[i] as SharpOS.AOT.IR.Operands.Indirect).Value;

					if (operand.IsRegisterSet) {
						Register register = Assembly.GetRegister (operand.Register);
						parameterTypes += register.GetType().Name;
						operands[i] = register;

					} else {
						Memory memory = this.GetMemory (operand as Identifier);
						parameterTypes += memory.GetType().Name;
						operands[i] = memory;
					}

				} else {
					parameterTypes += parameter.ParameterType.Name;

					operands[i] = call.Value.Operands[i];
				}
			}

			parameterTypes = call.Method.Method.Name + " " + parameterTypes;

			parameterTypes = parameterTypes.Trim();

			// Checking if the operands are all valid.
			foreach (object operand in operands) {
				if (operand is Identifier
						&& !operand.ToString ().StartsWith ("SharpOS.AOT.X86"))
					throw new Exception (string.Format ("'{0}' in '{1}' is containing wrong operands.", instruction, this.method.MethodFullName));
			}

			assembly.GetAssemblyInstruction (call.Method, operands, parameterTypes);

			if (call.Method.Method.Name.Equals ("LABEL"))
				this.assembly.AddSymbol (new COFF.Label (operands [0].ToString ()));
		}

		private void PushOperand (Operand operand)
		{
			if (operand.SizeType == Operand.InternalSizeType.ValueType) {
				SharpOS.AOT.IR.Operands.Object _object = null;
				Identifier identifier = operand as Identifier;
				int size = this.method.Engine.GetTypeSize (identifier.TypeName);

				uint pushSize = (uint) size;

				if (pushSize % 4 != 0)
					pushSize = ((pushSize / 4) + 1) * 4;

				this.assembly.SUB (R32.ESP, pushSize);

				this.assembly.PUSH (R32.ESI);
				this.assembly.PUSH (R32.EDI);
				this.assembly.PUSH (R32.ECX);

				if (operand is SharpOS.AOT.IR.Operands.Object) {
					if (IsFourBytes (_object.Address)) {
						if (_object.Address.IsRegisterSet)
							this.MovRegisterRegister (R32.ESI, Assembly.GetRegister (_object.Address.Register));

						else
							this.assembly.LEA (R32.ESI, this.GetMemory (_object.Address as Identifier));

					} else
						throw new Exception ("'" + _object.Address + "' is not supported.");

				} else
					this.assembly.LEA (R32.ESI, this.GetMemory (operand as Identifier));

				// The 3 push above changed the ESP so we need a LEA = ESP + 12
				this.assembly.LEA (R32.EDI, new Memory (null, R32.ESP, null, 0, 12));
				this.assembly.MOV (R32.ECX, (uint) size);

				this.assembly.CLD ();
				this.assembly.REP ();
				this.assembly.MOVSB ();

				this.assembly.POP (R32.ECX);
				this.assembly.POP (R32.EDI);
				this.assembly.POP (R32.ESI);

			} else if (operand is Constant) {
				if (IsFourBytes (operand)) {
					Int32 value = Convert.ToInt32 ((operand as Constant).Value);

					assembly.PUSH ((UInt32) value);

				} else if (IsEightBytes (operand)) {
					Int64 value = Convert.ToInt64 ((operand as Constant).Value);

					assembly.PUSH ((UInt32) (value >> 32));
					assembly.PUSH ((UInt32) (value & 0xFFFFFFFF));

				} else
					throw new Exception ("'" + operand + "' is not supported.");

			} else if (operand is Argument
				       || operand is SharpOS.AOT.IR.Operands.Register
				       || operand is Address
				       || operand is Indirect
				       || operand is Local) {
				if (IsFourBytes (operand)) {
					if (operand is Address
							&& (operand as Address).Value.SizeType == Operand.InternalSizeType.ValueType)
						operand = (operand as Address).Value;

					if (operand.IsRegisterSet)
						assembly.PUSH (Assembly.GetRegister (operand.Register));

					else {
						this.MovRegisterMemory (R32.EAX, operand as Identifier);
						assembly.PUSH (R32.EAX);
					}

				} else if (IsEightBytes (operand)) {
					DWordMemory memory = this.GetMemory (operand as Identifier) as DWordMemory;
					memory.DisplacementDelta = 4;
					assembly.PUSH (memory);

					memory = this.GetMemory (operand as Identifier) as DWordMemory;
					assembly.PUSH (memory);

				} else
					throw new Exception ("'" + operand + "' is not supported.");

			} else
				throw new Exception ("'" + operand + "' is not supported.");
		}

		private void PushCallParameters (SharpOS.AOT.IR.Operands.Call call) 
		{
			for (int i = 0; i < call.Operands.Length; i++) {
				Operand operand = call.Operands [call.Operands.Length - i - 1];

				this.PushOperand (operand);
			}
		}

		private void PopCallParameters (SharpOS.AOT.IR.Operands.Call call)
		{
			uint result = 0;

			foreach (ParameterDefinition parameter in call.Method.Parameters)
				result += (uint) this.method.Engine.GetTypeSize (parameter.ParameterType.ToString (), 4);

			if (call.Method.HasThis)
				result += 4;

			TypeDefinition returnType = call.Method.ReturnType.ReturnType as TypeDefinition;

			// In case the return type is a structure in that case the last parameter that is pushed on the stack
			// is the address to the memory where the result gets copied, and that address has to be pulled from 
			// the stack.
			if (returnType != null && returnType.IsValueType)
				result += 4;			

			assembly.ADD (R32.ESP, result);
		}
		/// <summary>
		/// Handles the call.
		/// </summary>
		/// <param name="block">The block.</param>
		/// <param name="call">The call.</param>
		private void HandleCall (Block block, SharpOS.AOT.IR.Operands.Call call)
		{
			PushCallParameters (call);

			assembly.CALL (call.AssemblyLabel);

			PopCallParameters (call);
		}

		/// <summary>
		/// Movs the register operand.
		/// </summary>
		/// <param name="register">The register.</param>
		/// <param name="operand">The operand.</param>
		private void MovRegisterOperand (R32Type register, Operand operand)
		{
			if (operand is Constant) {
				this.MovRegisterConstant (register, operand as Constant);

			} else if (operand.IsRegisterSet) {
				this.MovRegisterRegister (register, Assembly.GetRegister (operand.Register));

			} else
				this.MovRegisterMemory (register, operand as Identifier);
		}

		/// <summary>
		/// Movs the register operand.
		/// </summary>
		/// <param name="loRegister">The lo register.</param>
		/// <param name="hiRegister">The hi register.</param>
		/// <param name="operand">The operand.</param>
		private void MovRegisterOperand (R32Type loRegister, R32Type hiRegister, Operand operand)
		{
			if (operand is Constant) {
				Int64 value = Convert.ToInt64 ((operand as Constant).Value);

				this.MovRegisterConstant (loRegister, hiRegister, (UInt64) value);

			} else if (!operand.IsRegisterSet) {
				this.MovRegisterMemory (loRegister, hiRegister, operand as Identifier);

			} else if (IsFourBytes (operand)) {
				this.MovRegisterOperand (loRegister, operand);
				this.assembly.XOR (hiRegister, hiRegister);

			} else
				throw new Exception ("'" + operand + "' not supported.");
		}

		/// <summary>
		/// Movs the operand register.
		/// </summary>
		/// <param name="operand">The operand.</param>
		/// <param name="register">The register.</param>
		private void MovOperandRegister (Operand operand, R32Type register)
		{
			if (operand.IsRegisterSet)
				this.MovRegisterRegister (Assembly.GetRegister (operand.Register), register);

			else
				this.MovMemoryRegister (operand as Identifier, register);
		}

		/// <summary>
		/// Movs the register memory.
		/// </summary>
		/// <param name="register">The register.</param>
		/// <param name="identifier">The identifier.</param>
		private void MovRegisterMemory (R32Type register, Identifier identifier)
		{
			Memory memory = this.GetMemory (identifier);

			if (identifier.SizeType == Operand.InternalSizeType.ValueType) {
				// If it is "this" we need the address stored on the stack
				if (this.method.MethodDefinition.HasThis
						&& identifier is Argument
						&& (identifier as Argument).Index == 1)
					assembly.MOV (register, memory as DWordMemory);

				else if (identifier is SharpOS.AOT.IR.Operands.Object)
					assembly.MOV (register, memory as DWordMemory);

				else
					// If it is a Value Type we need only the address of the beginning of the object on the stack
					assembly.LEA (register, memory);

			} else if (memory is DWordMemory)
				assembly.MOV (register, memory as DWordMemory);

			else if (memory is WordMemory) {
				if (IsSigned (identifier))
					assembly.MOVSX (register, memory as WordMemory);

				else
					assembly.MOVZX (register, memory as WordMemory);

			} else if (memory is ByteMemory) {
				if (IsSigned (identifier))
					assembly.MOVSX (register, memory as ByteMemory);

				else
					assembly.MOVZX (register, memory as ByteMemory);

			} else
				throw new Exception ("'" + memory.ToString () + "' is not supported.");
		}

		/// <summary>
		/// Movs the register memory.
		/// </summary>
		/// <param name="loRegister">The lo register.</param>
		/// <param name="hiRegister">The hi register.</param>
		/// <param name="identifier">The identifier.</param>
		private void MovRegisterMemory (R32Type loRegister, R32Type hiRegister, Identifier identifier)
		{
			DWordMemory memory = this.GetMemoryType (identifier) as DWordMemory;
			memory.DisplacementDelta = 4;
			assembly.MOV (hiRegister, memory);

			memory = this.GetMemoryType (identifier) as DWordMemory;
			assembly.MOV (loRegister, memory);
		}

		/// <summary>
		/// Movs the memory register.
		/// </summary>
		/// <param name="identifier">The identifier.</param>
		/// <param name="loRegister">The lo register.</param>
		/// <param name="hiRegister">The hi register.</param>
		private void MovMemoryRegister (Identifier identifier, R32Type loRegister, R32Type hiRegister)
		{
			DWordMemory memory = this.GetMemoryType (identifier) as DWordMemory;
			memory.DisplacementDelta = 4;
			assembly.MOV (memory, hiRegister);
			
			memory = this.GetMemoryType (identifier) as DWordMemory;
			assembly.MOV (memory, loRegister);
		}

		/// <summary>
		/// Movs the memory register.
		/// </summary>
		/// <param name="identifier">The identifier.</param>
		/// <param name="register">The register.</param>
		private void MovMemoryRegister (Identifier identifier, R32Type register)
		{
			Memory memory = this.GetMemoryType (identifier);

			if (memory is ByteMemory) {
				R32Type spare = assembly.GetSpareRegister();
				ByteMemory byteMemory = this.GetMemory (identifier) as ByteMemory;

				this.MovRegisterRegister (spare, register);
				assembly.MOV (byteMemory, Assembly.Get8BitRegister (spare));

				assembly.FreeSpareRegister (spare);

			} else if (memory is WordMemory) {
				R32Type spare = assembly.GetSpareRegister();
				WordMemory wordMemory = this.GetMemory (identifier) as WordMemory;

				this.MovRegisterRegister (spare, register);
				assembly.MOV (wordMemory, Assembly.Get16BitRegister (spare));

				assembly.FreeSpareRegister (spare);

			} else if (memory is DWordMemory) {
				memory = this.GetMemory (identifier);

				assembly.MOV (memory as DWordMemory, register);

				if (IsEightBytes (identifier)) {
					memory = this.GetMemory (identifier);
					memory.DisplacementDelta = 4;
					assembly.MOV (memory as DWordMemory, 0);
				}

			} else
				throw new Exception ("'" + memory.ToString() + "' is not supported.");
		}

		/// <summary>
		/// CMPs the specified type.
		/// </summary>
		/// <param name="type">The type.</param>
		/// <param name="first">The first.</param>
		/// <param name="second">The second.</param>
		/// <param name="okLabel">The ok label.</param>
		/// <param name="errorLabel">The error label.</param>
		/// <param name="endLabel">The end label.</param>
		private void CMP (SharpOS.AOT.IR.Operators.Boolean.RelationalType type, Operand first, Operand second, string okLabel, string errorLabel, string endLabel)
		{
			this.MovRegisterOperand (R32.EAX, R32.EDX, first);

			if (second is Constant) {
				Int64 constant = Convert.ToInt64 ((second as Constant).Value);

				assembly.CMP (R32.EDX, (UInt32) (constant >> 32));

			} else if ((second as Identifier).IsRegisterSet){
				assembly.CMP (R32.EDX, 0);

			} else {
				DWordMemory memory = this.GetMemory (second as Identifier) as DWordMemory;

				memory.DisplacementDelta = 4;

				assembly.CMP (R32.EDX, memory);
			}

			switch (type) {

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.Equal:
					assembly.JNE (errorLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.NotEqualOrUnordered:
					assembly.JNE (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.LessThan:
					assembly.JG (errorLabel);

					assembly.JL (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.LessThanUnsignedOrUnordered:
					assembly.JA (errorLabel);

					assembly.JB (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.LessThanOrEqual:
					assembly.JG (errorLabel);

					assembly.JL (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.LessThanOrEqualUnsignedOrUnordered:
					assembly.JA (errorLabel);

					assembly.JB (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.GreaterThan:
					assembly.JL (errorLabel);

					assembly.JG (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.GreaterThanUnsignedOrUnordered:
					assembly.JB (errorLabel);

					assembly.JA (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.GreaterThanOrEqual:
					assembly.JL (errorLabel);

					assembly.JG (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.GreaterThanOrEqualUnsignedOrUnordered:
					assembly.JB (errorLabel);

					assembly.JA (okLabel);

					break;

				default:
					throw new Exception ("'" + type + "' is not supported.");
			}

			if (second is Constant) {
				Int64 constant = Convert.ToInt64 ( (second as Constant).Value);

				assembly.CMP (R32.EAX, (UInt32) (constant & 0xFFFFFFFF));

			} else if ((second as Identifier).IsRegisterSet) {
				assembly.CMP (R32.EAX, Assembly.GetRegister (second.Register));

			} else {
				Memory memory = this.GetMemory (second as Identifier);

				assembly.CMP (R32.EAX, memory as DWordMemory);
			}

			switch (type) {

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.Equal:
					assembly.JE (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.NotEqualOrUnordered:
					assembly.JNE (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.LessThan:
					assembly.JB (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.LessThanUnsignedOrUnordered:
					assembly.JB (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.LessThanOrEqual:
					assembly.JBE (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.LessThanOrEqualUnsignedOrUnordered:
					assembly.JBE (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.GreaterThan:
					assembly.JA (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.GreaterThanUnsignedOrUnordered:
					assembly.JA (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.GreaterThanOrEqual:
					assembly.JAE (okLabel);

					break;

				case SharpOS.AOT.IR.Operators.Boolean.RelationalType.GreaterThanOrEqualUnsignedOrUnordered:
					assembly.JAE (okLabel);

					break;

				default:
					throw new Exception ("'" + type + "' is not supported.");
			}
		}

		/// <summary>
		/// Movs the register boolean.
		/// </summary>
		/// <param name="register">The register.</param>
		/// <param name="operand">The operand.</param>
		private void MovRegisterBoolean (R32Type register, SharpOS.AOT.IR.Operands.Boolean operand)
		{
			SharpOS.AOT.IR.Operators.Boolean.RelationalType type = (operand.Operator as SharpOS.AOT.IR.Operators.Relational).Type;

			Operand first = operand.Operands[0];
			Operand second = operand.Operands[1];

			if (first is Constant) {
				Operand temp = first;
				first = second;
				second = temp;
			}

			if (IsEightBytes (first)
					|| IsEightBytes (second)) {
				string errorLabel = assembly.GetCMPLabel;
				string okLabel = assembly.GetCMPLabel;
				string endLabel = assembly.GetCMPLabel;

				this.CMP (type, first, second, okLabel, errorLabel, endLabel);

				assembly.LABEL (errorLabel);
				this.MovRegisterConstant (register, 0);
				assembly.JMP (endLabel);

				assembly.LABEL (okLabel);
				this.MovRegisterConstant (register, 1);

				assembly.LABEL (endLabel);

			} else {
				this.MovRegisterOperand (register, first);

				if (second is Constant) {
					Int32 value = Convert.ToInt32 ( (second as Constant).Value);

					assembly.CMP (register, (UInt32) value);

				} else if (second.IsRegisterSet) {
					assembly.CMP (register, Assembly.GetRegister (second.Register));

				} else {
					R32Type spare = assembly.GetSpareRegister ();

					this.MovRegisterMemory (spare, second as Identifier);

					assembly.CMP (register, spare);

					assembly.FreeSpareRegister (spare);
				}

				switch (type) {

					case SharpOS.AOT.IR.Operators.Boolean.RelationalType.Equal:
						assembly.SETE (R8.AL);

						break;

					case SharpOS.AOT.IR.Operators.Boolean.RelationalType.GreaterThan:
						assembly.SETG (R8.AL);

						break;

					case SharpOS.AOT.IR.Operators.Boolean.RelationalType.GreaterThanUnsignedOrUnordered:
						assembly.SETA (R8.AL);

						break;

					case SharpOS.AOT.IR.Operators.Boolean.RelationalType.LessThan:
						assembly.SETL (R8.AL);

						break;

					case SharpOS.AOT.IR.Operators.Boolean.RelationalType.LessThanUnsignedOrUnordered:
						assembly.SETB (R8.AL);

						break;

					default:
						throw new Exception ("'" + operand.Operator + "' is not supported.");
				}

				assembly.MOVZX (register, R8.AL);
			}
		}

		/// <summary>
		/// Movs the register arithmetic.
		/// </summary>
		/// <param name="loRegister">The lo register.</param>
		/// <param name="hiRegister">The hi register.</param>
		/// <param name="operand">The operand.</param>
		private void MovRegisterArithmetic (R32Type loRegister, R32Type hiRegister, Arithmetic operand)
		{
			if (operand.Operator is Unary) {
				Unary.UnaryType type = (operand.Operator as Unary).Type;
				Operand first = operand.Operands[0];

				this.MovRegisterOperand (loRegister, hiRegister, first);

				if (type == Operator.UnaryType.Negation) {
					assembly.NEG (loRegister);
					assembly.NEG (hiRegister);

				} else if (type == Operator.UnaryType.Not) {
					assembly.NOT (loRegister);
					assembly.NOT (hiRegister);

				} else {
					throw new Exception ("'" + type + "' is not supported.");
				}

			} else if (operand.Operator is Binary) {
				Binary.BinaryType type = (operand.Operator as Binary).Type;
				Operand first = operand.Operands[0];
				Operand second = operand.Operands[1];

				this.MovRegisterOperand (loRegister, hiRegister, first);

				// TODO validate the parameter types int32 & int32, int64 & int64.....
				// TODO add register support not only constant and memory
				if (type == Binary.BinaryType.Add) {
					if (second is Constant) {
						Int64 value = Convert.ToInt64 ( (second as Constant).Value);

						UInt32 loConstant = (UInt32) (value & 0xFFFFFFFF);
						UInt32 hiConstant = (UInt32) (value >> 32);

						assembly.ADD (loRegister, loConstant);
						assembly.ADC (hiRegister, hiConstant);

					} else if (!second.IsRegisterSet) {
						DWordMemory memory = this.GetMemoryType (second as Identifier) as DWordMemory;

						assembly.ADD (loRegister, memory);

						memory = new DWordMemory (memory);
						memory.DisplacementDelta = 4;

						assembly.ADC (hiRegister, memory);

					} else
						throw new Exception ("'" + second + "' is not supported.");

				} else if (type == Operator.BinaryType.Sub) {
					if (second is Constant) {
						Int64 value = Convert.ToInt64 ( (second as Constant).Value);

						UInt32 loConstant = (UInt32) (value & 0xFFFFFFFF);
						UInt32 hiConstant = (UInt32) (value >> 32);

						assembly.SUB (loRegister, loConstant);
						assembly.SBB (hiRegister, hiConstant);

					} else if (!second.IsRegisterSet) {
						DWordMemory memory = this.GetMemoryType (second as Identifier) as DWordMemory;

						assembly.SUB (loRegister, memory);

						memory = new DWordMemory (memory);
						memory.DisplacementDelta = 4;

						assembly.SBB (hiRegister, memory);

					} else
						throw new Exception ("'" + second + "' is not supported.");

				} else if (type == Operator.BinaryType.And) {
					if (second is Constant) {
						Int64 value = Convert.ToInt64 ( (second as Constant).Value);

						UInt32 loConstant = (UInt32) (value & 0xFFFFFFFF);
						UInt32 hiConstant = (UInt32) (value >> 32);

						assembly.AND (loRegister, loConstant);
						assembly.AND (hiRegister, hiConstant);

					} else if (second.IsRegisterSet) {
						assembly.AND (loRegister, Assembly.GetRegister (second.Register));
						assembly.AND (hiRegister, 0);

					} else if (!second.IsRegisterSet) {
						DWordMemory memory = this.GetMemoryType (second as Identifier) as DWordMemory;

						assembly.AND (loRegister, memory);

						memory = new DWordMemory (memory);
						memory.DisplacementDelta = 4;

						assembly.AND (hiRegister, memory);

					} else
						throw new Exception ("'" + second + "' is not supported.");

				} else if (type == Operator.BinaryType.Or) {
					if (second is Constant) {
						Int64 value = Convert.ToInt64 ( (second as Constant).Value);

						UInt32 loConstant = (UInt32) (value & 0xFFFFFFFF);
						UInt32 hiConstant = (UInt32) (value >> 32);

						assembly.OR (loRegister, loConstant);
						assembly.OR (hiRegister, hiConstant);

					} else if (!second.IsRegisterSet) {
						DWordMemory memory = this.GetMemoryType (second as Identifier) as DWordMemory;

						assembly.OR (loRegister, memory);

						memory = new DWordMemory (memory);
						memory.DisplacementDelta = 4;

						assembly.OR (hiRegister, memory);

					} else
						throw new Exception ("'" + second + "' is not supported.");

				} else if (type == Operator.BinaryType.SHL
						|| type == Operator.BinaryType.SHR
						|| type == Operator.BinaryType.SHRUnsigned) {
					
					// Only the lower 32-bit are needed for the second shift parameter
					if (second is Constant) {
						Int64 value = Convert.ToInt64 ( (second as Constant).Value);

						UInt32 loConstant = (UInt32) (value & 0xFFFFFFFF);

						assembly.PUSH (loConstant);

					} else if (second.IsRegisterSet) {
						assembly.PUSH (Assembly.GetRegister (second.Register));

					} else if (!second.IsRegisterSet) {
						DWordMemory memory = this.GetMemoryType (second as Identifier) as DWordMemory;

						assembly.PUSH (memory);

					} else
						throw new Exception ("'" + second + "' is not supported.");

					assembly.PUSH (hiRegister);

					assembly.PUSH (loRegister);

					if (type == Operator.BinaryType.SHL) {
						assembly.CALL (Assembly.HELPER_LSHL);

					} else if (type == Operator.BinaryType.SHR) {
						assembly.CALL (Assembly.HELPER_LSAR);

					} else if (type == Operator.BinaryType.SHRUnsigned) {
						assembly.CALL (Assembly.HELPER_LSHR);

					} else {
						throw new Exception ("'" + type + "' not supported.");
					}

					assembly.ADD (R32.ESP, 12);

					this.MovRegisterRegister (loRegister, R32.EAX);
					this.MovRegisterRegister (hiRegister, R32.EDX);

				} else
					throw new Exception ("'" + type + "' is not supported.");

			} else
				throw new Exception ("'" + operand.Operator + "' is not supported.");
		}

		/// <summary>
		/// Movs the register arithmetic.
		/// </summary>
		/// <param name="register">The register.</param>
		/// <param name="operand">The operand.</param>
		private void MovRegisterArithmetic (R32Type register, Arithmetic operand)
		{
			if (operand.Operator is Unary) {
				Unary.UnaryType type = (operand.Operator as Unary).Type;
				Operand first = operand.Operands[0];

				this.MovRegisterOperand (register, first);

				if (type == Operator.UnaryType.Negation) {
					assembly.NEG (register);

				} else if (type == Operator.UnaryType.Not) {
					assembly.NOT (register);

				} else {
					throw new Exception ("'" + type + "' is not supported.");
				}

			} else if (operand.Operator is Binary) {
				Binary.BinaryType type = (operand.Operator as Binary).Type;
				Operand first = operand.Operands[0];
				Operand second = operand.Operands[1];

				this.MovRegisterOperand (register, first);

				// TODO validate the parameter types int32 & int32, int64 & int64.....
				if (type == Binary.BinaryType.Add) {
					if (second is Constant) {
						Int32 value = Convert.ToInt32 ((second as Constant).Value);

						assembly.ADD (register, (UInt32) value);

					} else if (second.IsRegisterSet) {
						assembly.ADD (register, Assembly.GetRegister (second.Register));

					} else {
						R32Type spareRegister = this.assembly.GetSpareRegister ();

						this.MovRegisterMemory (spareRegister, second as Identifier);

						assembly.ADD (register, spareRegister);

						this.assembly.FreeSpareRegister (spareRegister);
					}

				} else if (type == Binary.BinaryType.Sub) {
					if (second is Constant) {
						UInt32 value = (uint) Convert.ToInt32 ( (second as Constant).Value);
						//UInt32 value = Convert.ToUInt32 (Convert.ToInt32 ( (second as Constant).Value));

						assembly.SUB (register, value);

					} else if (second.IsRegisterSet) {
						assembly.SUB (register, Assembly.GetRegister (second.Register));

					} else {
						R32Type spareRegister = this.assembly.GetSpareRegister ();

						this.MovRegisterMemory (spareRegister, second as Identifier);

						assembly.SUB (register, spareRegister);

						this.assembly.FreeSpareRegister (spareRegister);
					}

				} else if (type == Binary.BinaryType.Mul) {
					if (second is Constant) {
						UInt32 value = Convert.ToUInt32 (Convert.ToInt32 ( (second as Constant).Value));

						assembly.IMUL (register, value);

					} else if (second.IsRegisterSet) {
						assembly.IMUL (register, Assembly.GetRegister (second.Register));

					} else {
						R32Type spareRegister = this.assembly.GetSpareRegister ();

						this.MovRegisterMemory (spareRegister, second as Identifier);

						assembly.IMUL (register, spareRegister);

						this.assembly.FreeSpareRegister (spareRegister);
					}

				} else if (type == Binary.BinaryType.Div) {
					this.MovRegisterOperand (R32.EAX, first);
					this.MovRegisterOperand (R32.ECX, second);

					assembly.CDQ();
					assembly.IDIV (R32.ECX);

					assembly.MOV (register, R32.EAX);

				} else if (type == Binary.BinaryType.DivUnsigned) {
					this.MovRegisterOperand (R32.EAX, first);
					this.MovRegisterOperand (R32.ECX, second);
					this.assembly.XOR (R32.EDX, R32.EDX);

					assembly.DIV (R32.ECX);

					assembly.MOV (register, R32.EAX);

				} else if (type == Operator.BinaryType.Remainder) {
					this.MovRegisterOperand (R32.EAX, first);
					this.MovRegisterOperand (R32.ECX, second);

					assembly.CDQ ();
					assembly.IDIV (R32.ECX);

					assembly.MOV (register, R32.EDX);

				} else if (type == Operator.BinaryType.RemainderUnsigned) {
					this.MovRegisterOperand (R32.EAX, first);
					this.MovRegisterOperand (R32.ECX, second);
					this.assembly.XOR (R32.EDX, R32.EDX);

					assembly.DIV (R32.ECX);

					assembly.MOV (register, R32.EDX);

				} else if (type == Binary.BinaryType.And) {
					if (second is Constant) {
						UInt32 value = (UInt32) Convert.ToInt32 ( (second as Constant).Value);

						assembly.AND (register, value);

					} else if (second.IsRegisterSet) {
						assembly.AND (register, Assembly.GetRegister (second.Register));

					} else {
						R32Type spareRegister = this.assembly.GetSpareRegister ();

						this.MovRegisterMemory (spareRegister, second as Identifier);

						assembly.AND (register, spareRegister);

						this.assembly.FreeSpareRegister (spareRegister);
					}

				} else if (type == Binary.BinaryType.Or) {
					if (second is Constant) {
						UInt32 value = (uint) Convert.ToInt32 ( (second as Constant).Value);

						assembly.OR (register, value);

					} else if (second.IsRegisterSet) {
						assembly.OR (register, Assembly.GetRegister (second.Register));

					} else {
						R32Type spareRegister = this.assembly.GetSpareRegister ();

						this.MovRegisterMemory (spareRegister, second as Identifier);

						assembly.OR (register, spareRegister);

						this.assembly.FreeSpareRegister (spareRegister);
					}

				} else if (type == Binary.BinaryType.Xor) {
					if (second is Constant) {
						UInt32 value = (uint) Convert.ToInt32 ((second as Constant).Value);

						assembly.XOR (register, value);

					} else if (second.IsRegisterSet) {
						assembly.XOR (register, Assembly.GetRegister (second.Register));

					} else {
						R32Type spareRegister = this.assembly.GetSpareRegister ();

						this.MovRegisterMemory (spareRegister, second as Identifier);

						assembly.XOR (register, spareRegister);

						this.assembly.FreeSpareRegister (spareRegister);
					}

				} else if (type == Binary.BinaryType.SHL) {
					this.MovRegisterOperand (R32.ECX, second);

					assembly.SHL__CL (register);

				} else if (type == Binary.BinaryType.SHR) {
					this.MovRegisterOperand (R32.ECX, second);

					assembly.SAR__CL (register);

				} else if (type == Binary.BinaryType.SHRUnsigned) {
					this.MovRegisterOperand (R32.ECX, second);

					assembly.SHR__CL (register);

				} else
					throw new Exception ("'" + type + "' is not supported.");

			} else
				throw new Exception ("'" + operand.Operator + "' is not supported.");
		}

		/// <summary>
		/// Movs the register constant.
		/// </summary>
		/// <param name="register">The register.</param>
		/// <param name="constant">The constant.</param>
		private void MovRegisterConstant (R32Type register, UInt32 constant)
		{
			if (constant == 0)
				assembly.XOR (register, register);
			else
				assembly.MOV (register, constant);
		}

		/// <summary>
		/// Movs the register constant.
		/// </summary>
		/// <param name="loRegister">The lo register.</param>
		/// <param name="hiRegister">The hi register.</param>
		/// <param name="constant">The constant.</param>
		private void MovRegisterConstant (R32Type loRegister, R32Type hiRegister, UInt64 constant)
		{
			if (constant == 0) {
				assembly.XOR (loRegister, loRegister);
				assembly.XOR (hiRegister, hiRegister);

			} else {
				assembly.MOV (loRegister, (UInt32) (constant & 0xFFFFFFFF));
				assembly.MOV (hiRegister, (UInt32) (constant >> 32));
			}
		}

		/// <summary>
		/// Handles the assign.
		/// </summary>
		/// <param name="block">The block.</param>
		/// <param name="instruction">The instruction.</param>
		private void HandleAssign (Block block, SharpOS.AOT.IR.Instructions.Instruction instruction)
		{
			SharpOS.AOT.IR.Instructions.Assign assign = (instruction as SharpOS.AOT.IR.Instructions.Assign);

			if (assign.Value is Address) {
				Address address = assign.Value as Address;
				R32Type register;

				if (assign.Assignee.IsRegisterSet)
					register = Assembly.GetRegister (assign.Assignee.Register);

				else
					register = assembly.GetSpareRegister ();

				if (address.Value.IsRegisterSet)
					assembly.MOV (register, Assembly.GetRegister (address.Value.Register));

				else 
					assembly.LEA (register, this.GetMemory (address.Value));

				if (!assign.Assignee.IsRegisterSet) {
					this.MovMemoryRegister (assign.Assignee, register);

					this.assembly.FreeSpareRegister (register);
				}

			} else if (assign.Value is Constant) {
				if (IsFourBytes (assign.Assignee)
						|| IsEightBytes (assign.Assignee)
						|| assign.Assignee.SizeType == Operand.InternalSizeType.ValueType) {

					if ((assign.Value as Constant).Value is string) {
						string resource = assembly.AddString (assign.Value.ToString ());

						if (assign.Assignee.IsRegisterSet)
							assembly.MOV (Assembly.GetRegister (assign.Assignee.Register), resource);

						else {
							R32Type spare = this.assembly.GetSpareRegister ();
							assembly.MOV (spare, resource);
							this.MovMemoryRegister (assign.Assignee, spare);
							this.assembly.FreeSpareRegister (spare);
						}

					} else {
						if (assign.Assignee.IsRegisterSet)
							this.MovRegisterConstant (assign);

						else
							this.MovMemoryConstant (assign);
					}

				} else
					throw new Exception ("'" + instruction + "' is not supported.");

			} else if (assign.Value is Identifier) {
				if (assign.Assignee.IsRegisterSet
						&& assign.Value.IsRegisterSet) {
					this.MovRegisterRegister (assign);

				} else if (!assign.Assignee.IsRegisterSet
						&& !assign.Value.IsRegisterSet) {
					if (IsFourBytes (assign.Assignee)
							|| IsEightBytes (assign.Assignee)
							|| assign.Assignee.SizeType == Operand.InternalSizeType.ValueType) {
						this.MovMemoryMemory (assign);

					} else
						throw new Exception ("'" + instruction + "' is not supported.");

				} else if (!assign.Assignee.IsRegisterSet
						&& assign.Value.IsRegisterSet) {
					if (IsFourBytes (assign.Assignee)
							|| IsEightBytes (assign.Assignee)) {
						this.MovMemoryRegister (assign);

					} else 
						throw new Exception ("'" + instruction + "' is not supported.");

				} else if (assign.Assignee.IsRegisterSet
						&& !assign.Value.IsRegisterSet) {
					if (IsFourBytes (assign.Assignee))
						this.MovRegisterMemory (assign);

					else
						throw new Exception ("'" + instruction + "' is not supported.");

				} else
					// Just in case....
					throw new Exception ("'" + instruction + "' is not supported.");

			} else if (assign.Value is Arithmetic) {
				if (IsFourBytes (assign.Assignee)) {
					if (assign.Assignee.IsRegisterSet)
						this.MovRegisterArithmetic (Assembly.GetRegister (assign.Assignee.Register), assign.Value as Arithmetic);

					else {
						R32Type register = assembly.GetSpareRegister();

						this.MovRegisterArithmetic (register, assign.Value as Arithmetic);

						this.MovMemoryRegister (assign.Assignee, register);

						assembly.FreeSpareRegister (register);
					}

				} else if (IsEightBytes (assign.Assignee)) {
					this.MovRegisterArithmetic (R32.EAX, R32.EDX, assign.Value as Arithmetic);

					this.MovMemoryRegister (assign.Assignee, R32.EAX, R32.EDX);

				} else
					throw new Exception ("'" + instruction + "' is not supported.");

			} else if (assign.Value is SharpOS.AOT.IR.Operands.Boolean) {
				if (IsFourBytes (assign.Assignee)) {
					if (assign.Assignee.IsRegisterSet) {
						this.MovRegisterBoolean (Assembly.GetRegister (assign.Assignee.Register), assign.Value as SharpOS.AOT.IR.Operands.Boolean);

					} else {
						R32Type register = assembly.GetSpareRegister();

						this.MovRegisterBoolean (register, assign.Value as SharpOS.AOT.IR.Operands.Boolean);

						this.MovMemoryRegister (assign.Assignee, register);

						assembly.FreeSpareRegister (register);
					}

				} else
					throw new Exception ("'" + instruction + "' is not supported.");

			} else if (assign.Value is SharpOS.AOT.IR.Operands.Call) {
				SharpOS.AOT.IR.Operands.Call call = assign.Value as SharpOS.AOT.IR.Operands.Call;

				if (Assembly.IsKernelString (call)) {
					assembly.UTF7StringEncoding = true;
					this.HandleAssign (block, new Assign (assign.Assignee, call.Operands[0]));
					assembly.UTF7StringEncoding = false;

				} else if (Assembly.IsKernelAlloc (call)) {
					if (assign.Assignee.IsRegisterSet)
						this.assembly.MOV (Assembly.GetRegister (assign.Assignee.Register), this.assembly.BSSAlloc (Convert.ToUInt32 ((call.Operands [0] as SharpOS.AOT.IR.Operands.Constant).Value)));

					else {
						R32Type register = this.assembly.GetSpareRegister ();

						this.assembly.MOV (register, this.assembly.BSSAlloc (Convert.ToUInt32 ((call.Operands [0] as SharpOS.AOT.IR.Operands.Constant).Value)));

						this.MovMemoryRegister (assign.Assignee, register);

						this.assembly.FreeSpareRegister (register);
					}

				} else if (Assembly.IsKernelLabelledAlloc (call)) {
					if (assign.Assignee.IsRegisterSet)
						this.assembly.MOV (Assembly.GetRegister (assign.Assignee.Register), this.assembly.BSSAlloc ((call.Operands [0] as SharpOS.AOT.IR.Operands.Constant).Value.ToString (), Convert.ToUInt32 ((call.Operands [1] as SharpOS.AOT.IR.Operands.Constant).Value)));

					else {
						R32Type register = this.assembly.GetSpareRegister ();

						this.assembly.MOV (register, this.assembly.BSSAlloc ((call.Operands [0] as SharpOS.AOT.IR.Operands.Constant).Value.ToString (), Convert.ToUInt32 ((call.Operands [1] as SharpOS.AOT.IR.Operands.Constant).Value)));

						this.MovMemoryRegister (assign.Assignee, register);

						this.assembly.FreeSpareRegister (register);
					}

				} else if (Assembly.IsKernelLabelAddress (call)) {
					if (assign.Assignee.IsRegisterSet)
						this.assembly.MOV (Assembly.GetRegister (assign.Assignee.Register), (call.Operands [0] as SharpOS.AOT.IR.Operands.Constant).Value.ToString ());

					else {
						R32Type register = this.assembly.GetSpareRegister ();

						this.assembly.MOV (register, (call.Operands [0] as SharpOS.AOT.IR.Operands.Constant).Value.ToString ());

						this.MovMemoryRegister (assign.Assignee, register);

						this.assembly.FreeSpareRegister (register);
					}

				} else {
					// A special case for NewObj that returns an address 
					if (call.Method is MethodDefinition
							&& (call.Method as MethodDefinition).IsConstructor) {

						int size = this.method.Engine.GetTypeSize (call.Method.DeclaringType.ToString (), 4);
					
						this.assembly.SUB (R32.ESP, (uint) size);

						this.MovOperandRegister (assign.Assignee, R32.ESP);

						this.Initialize (assign.Assignee);

						this.PushCallParameters (call);

						this.PushOperand (assign.Assignee);

						this.assembly.CALL (call.AssemblyLabel);

						this.PopCallParameters (call);

					} else {
						TypeDefinition returnType = call.Method.ReturnType.ReturnType as TypeDefinition;

						if (returnType != null && returnType.IsValueType) {
							this.PushCallParameters (call);

							this.assembly.LEA (R32.EAX, this.GetMemory (assign.Assignee as Identifier));
							this.assembly.PUSH (R32.EAX);

							this.assembly.CALL (call.AssemblyLabel);

							this.PopCallParameters (call);

						} else {

							this.HandleCall (block, call);

							if (IsFourBytes (assign.Assignee)) {
								this.MovOperandRegister (assign.Assignee, R32.EAX);

							} else if (IsEightBytes (assign.Assignee)) {
								this.MovMemoryRegister (assign.Assignee, R32.EAX, R32.EDX);

							} else
								throw new Exception ("'" + instruction + "' is not supported.");
						}
					}
				}

			} else if (assign.Value is SharpOS.AOT.IR.Operands.Miscellaneous) {
				SharpOS.AOT.IR.Operands.Miscellaneous miscellaneous = assign.Value as SharpOS.AOT.IR.Operands.Miscellaneous;

				if (miscellaneous.Operator is SharpOS.AOT.IR.Operators.Miscellaneous
						&& (miscellaneous.Operator as SharpOS.AOT.IR.Operators.Miscellaneous).Type == Operator.MiscellaneousType.Localloc) {

					if (miscellaneous.Operands [0] is Constant) {					
						int size = Convert.ToInt32 ((miscellaneous.Operands [0] as Constant).Value);

						if (size < 1)
							throw new Exception ("'" + instruction + "' has an invalid size value.");

						if (size > 4096)
							throw new Exception ("'" + instruction + "' has an invalid size value. (Bigger than 4096 bytes)");

						if (size % 4 != 0)
							size = ((size / 4) + 1) * 4;

						this.assembly.SUB (R32.ESP, (uint) size);

					} else if (miscellaneous.Operands [0] is Identifier) {
						this.MovRegisterOperand (R32.EAX, miscellaneous.Operands [0]);

						// TODO: verify size
						this.assembly.SUB (R32.ESP, R32.EAX);

					} else {
						throw new Exception ("'" + miscellaneous.Operands [0].GetType () + "' is not supported'");
					}
					
					this.MovOperandRegister (assign.Assignee, R32.ESP);

				} else
					throw new Exception ("'" + instruction + "' is not supported.");

			} else if (assign is Initialize) {
				Initialize initialize = assign as Initialize;

				if (!assign.Assignee.IsRegisterSet)
					this.Initialize (assign.Assignee);

				else
					throw new Exception ("'" + instruction + "' is not supported.");

			} else
				throw new Exception ("'" + instruction + "' is not supported.");
		}

		/// <summary>
		/// Handles the return.
		/// </summary>
		/// <param name="block">The block.</param>
		/// <param name="instruction">The instruction.</param>
		private void HandleReturn (Block block, SharpOS.AOT.IR.Instructions.Return instruction)
		{
			if (instruction.Value != null) {
				TypeDefinition returnType = block.Method.MethodDefinition.ReturnType.ReturnType as TypeDefinition;
					
				if (IsFourBytes (instruction.Value)) {
					this.MovRegisterOperand (R32.EAX, instruction.Value);

				} else if (IsEightBytes (instruction.Value)) {
					this.MovRegisterMemory (R32.EAX, R32.EDX, instruction.Value as Identifier);

				} else if (returnType != null && returnType.IsValueType) {
					int size = this.method.Engine.GetTypeSize (returnType.FullName, 4) / 4;

					this.assembly.PUSH (R32.ECX);
					this.assembly.PUSH (R32.ESI);
					this.assembly.PUSH (R32.EDI);

					this.MovRegisterOperand (R32.ESI, instruction.Value);
					this.assembly.MOV (R32.EDI, new DWordMemory (null, R32.EBP, null, 0, 8));

					this.assembly.MOV (R32.ECX, (uint) size);

					this.assembly.CLD ();
					this.assembly.REP ();
					this.assembly.MOVSD ();

					this.assembly.POP (R32.EDI);
					this.assembly.POP (R32.ESI);
					this.assembly.POP (R32.ECX);

				} else
					throw new Exception ("'" + instruction + "' is not supported.");
			}

			assembly.JMP (method.MethodFullName + " exit");
		}

		private void Initialize (Identifier identifier)
		{
			int size = this.method.Engine.GetTypeSize (identifier.TypeName, 4) / 4;

			if (size == 1) {
				this.assembly.XOR (R32.EAX, R32.EAX);
				this.MovOperandRegister (identifier, R32.EAX);

			} else {
				this.assembly.PUSH (R32.ECX);
				this.assembly.PUSH (R32.EDI);

				this.assembly.XOR (R32.EAX, R32.EAX);
				this.MovRegisterOperand (R32.EDI, identifier);
				this.assembly.MOV (R32.ECX, (uint) size);

				this.assembly.CLD ();
				this.assembly.REP ();
				this.assembly.STOSD ();

				this.assembly.POP (R32.EDI);
				this.assembly.POP (R32.ECX);
			}
		}

		private void HandlerSystem (Block block, SharpOS.AOT.IR.Instructions.System instruction)
		{
			if (instruction.Value is IR.Operands.Miscellaneous
					&& (instruction.Value as IR.Operands.Miscellaneous).Operator is IR.Operators.Miscellaneous
					&& ((instruction.Value as IR.Operands.Miscellaneous).Operator as IR.Operators.Miscellaneous).Type == IR.Operators.Miscellaneous.MiscellaneousType.InitObj) {
				IR.Operands.Miscellaneous miscellaneous = instruction.Value as IR.Operands.Miscellaneous;
				Identifier identifier = miscellaneous.Operands [0] as Identifier;

				this.Initialize (identifier);

			} else
				throw new Exception ("'" + instruction + "' is not supported.");
		}

		/// <summary>
		/// Movs the register constant.
		/// </summary>
		/// <param name="register">The register.</param>
		/// <param name="operand">The operand.</param>
		private void MovRegisterConstant (R32Type register, Constant operand)
		{
			Int32 value = Convert.ToInt32 (operand.Value);
			this.MovRegisterConstant (register, (UInt32) value);
		}

		/// <summary>
		/// Movs the register constant.
		/// </summary>
		/// <param name="assign">The assign.</param>
		private void MovRegisterConstant (Assign assign)
		{
			this.MovRegisterConstant (Assembly.GetRegister (assign.Assignee.Register), assign.Value as Constant);
		}

		/// <summary>
		/// Movs the memory constant.
		/// </summary>
		/// <param name="assign">The assign.</param>
		private void MovMemoryConstant (Assign assign)
		{
			if (IsFourBytes (assign.Assignee)) {
				Memory memory = this.GetMemory (assign.Assignee);

				Int32 value = Convert.ToInt32 ( (assign.Value as Constant).Value);

				this.MovMemoryConstant (memory, (UInt32) value);

			} else if (IsEightBytes (assign.Assignee)) {
				Int64 value = Convert.ToInt64 ((assign.Value as Constant).Value);

				UInt32 hiValue = (UInt32) (value >> 32);
				UInt32 loValue = (UInt32) (value & 0xFFFFFFFF);

				DWordMemory memory = this.GetMemory (assign.Assignee) as DWordMemory;
				memory.DisplacementDelta = 4;
				this.MovMemoryConstant (memory, (UInt32) hiValue);

				memory = this.GetMemory (assign.Assignee) as DWordMemory;
				this.MovMemoryConstant (memory, (UInt32) loValue);

			} else {
				throw new Exception ("'" + assign.ToString() + "' not supported.");
			}
		}

		/// <summary>
		/// Gets the memory.
		/// </summary>
		/// <param name="sizeType">Type of the size.</param>
		/// <param name="_base">The _base.</param>
		/// <param name="scale">The scale.</param>
		/// <param name="displacement">The displacement.</param>
		/// <returns></returns>
		private Memory GetMemory (SharpOS.AOT.IR.Operands.Operand.InternalSizeType sizeType, R32Type _base, byte scale, int displacement)
		{
			return GetMemory (sizeType, _base, scale, displacement, string.Empty);
		}

		/// <summary>
		/// Gets the memory.
		/// </summary>
		/// <param name="sizeType">Type of the size.</param>
		/// <param name="_base">The _base.</param>
		/// <param name="scale">The scale.</param>
		/// <param name="displacement">The displacement.</param>
		/// <param name="label">The label.</param>
		/// <returns></returns>
		private static Memory GetMemory (SharpOS.AOT.IR.Operands.Operand.InternalSizeType sizeType, R32Type _base, byte scale, int displacement, string label)
		{
			Memory address = null;

			if (sizeType == Operand.InternalSizeType.I1
					|| sizeType == Operand.InternalSizeType.U1) {
				if (label.Length > 0)
					address = new ByteMemory (label);

				else {
					if (displacement == 0)
						address = new ByteMemory (null, _base, null, scale);

					else
						address = new ByteMemory (null, _base, null, scale, displacement);
				}

			} else if (sizeType == Operand.InternalSizeType.I2
					|| sizeType == Operand.InternalSizeType.U2) {
				if (label.Length > 0) {
					address = new WordMemory (label);

				} else {
					if (displacement == 0)
						address = new WordMemory (null, _base, null, scale);

					else
						address = new WordMemory (null, _base, null, scale, displacement);
				}

			} else if (sizeType == Operand.InternalSizeType.I4
					|| sizeType == Operand.InternalSizeType.U4
					|| sizeType == Operand.InternalSizeType.I
					|| sizeType == Operand.InternalSizeType.U
					|| sizeType == Operand.InternalSizeType.ValueType
					|| sizeType == Operand.InternalSizeType.S) {
				if (label.Length > 0)
					address = new DWordMemory (label);

				else {
					if (displacement == 0)
						address = new DWordMemory (null, _base, null, scale);

					else
						address = new DWordMemory (null, _base, null, scale, displacement);
				}

			} else if (sizeType == Operand.InternalSizeType.I8
					|| sizeType == Operand.InternalSizeType.U8) {
				if (label.Length > 0)
					address = new DWordMemory (label);

				else {
					if (displacement == 0)
						address = new DWordMemory (null, _base, null, scale);

					else
						address = new DWordMemory (null, _base, null, scale, displacement);
				}

			} else
				throw new Exception ("'" + sizeType + "' not supported.");

			return address;
		}

		/// <summary>
		/// Gets the type of the memory.
		/// </summary>
		/// <param name="operand">The operand.</param>
		/// <returns></returns>
		private Memory GetMemoryType (Identifier operand)
		{
			return this.GetMemory (operand, false);
		}

		/// <summary>
		/// Gets the memory.
		/// </summary>
		/// <param name="operand">The operand.</param>
		/// <returns></returns>
		private Memory GetMemory (Identifier operand)
		{
			return this.GetMemory (operand, true);
		}

		/// <summary>
		/// Gets the memory.
		/// </summary>
		/// <param name="operand">The operand.</param>
		/// <param name="emit">if set to <c>true</c> [emit].</param>
		/// <returns></returns>
		private Memory GetMemory (Identifier operand, bool emit)
		{
			Memory address = null;

			if (operand is Field) {
				Field field = operand as Field;

				if (field.Instance != null) {
					Identifier identifier = field.Instance as Identifier;
					R32Type register;

					if (identifier.IsRegisterSet)
						register = Assembly.GetRegister (identifier.Register);

					else {
						register = assembly.GetSpareRegister ();

						if (emit)
							this.MovRegisterMemory (register, identifier);
					}

					address = this.GetMemory (operand.SizeType, register, 0, this.assembly.GetFieldOffset (field.Value));

					assembly.FreeSpareRegister (register);

				} else
					address = GetMemory (operand.SizeType, null, 0, 0, (operand as Field).Value);

			} else if (operand is Indirect) {
				// TODO verify

				Identifier identifier = (operand as Indirect).Value as Identifier;
				R32Type register;

				if (identifier.IsRegisterSet)
					register = Assembly.GetRegister (identifier.Register);

				else {
					register = assembly.GetSpareRegister ();

					if (emit)
						this.MovRegisterMemory (register, identifier);
				}

				address = this.GetMemory (operand.SizeType, register, 0, 0);

				assembly.FreeSpareRegister (register);

			} else if (operand is Argument) {
				int index = (operand as SharpOS.AOT.IR.Operands.Argument).Index;

				address = this.GetMemory (operand.SizeType, R32.EBP, 0, this.GetArgumentOffset (index) * 4);

			} else if (operand.Stack != int.MinValue) {
				address = this.GetMemory (operand.SizeType, R32.EBP, 0, -((3 + operand.Stack) * 4));
			
			} else
				throw new Exception ("Wrong '" + operand.ToString () + "' Operand.");

			return address;
		}

		/// <summary>
		/// Gets the argument offset.
		/// </summary>
		/// <param name="index">The index.</param>
		/// <returns></returns>
		private int GetArgumentOffset (int index)
		{
			int i = 0;
			int result = 2; // EIP (of the caller) + EBP

			// If the return type is a value type then it will get the address for the buffer where the result is saved
			if (this.method.MethodDefinition.ReturnType.ReturnType is TypeDefinition
					&& (this.method.MethodDefinition.ReturnType.ReturnType as TypeDefinition).IsValueType)
				result++;

			foreach (ParameterDefinition parameter in this.method.MethodDefinition.Parameters) {
				Operand.InternalSizeType sizeType = this.method.Engine.GetInternalType (parameter.ParameterType.ToString());

				if (++i == index)
					break;

				result += this.method.Engine.GetTypeSize (parameter.ParameterType.ToString (), 4) >> 2;
			}

			return result;
		}

		/// <summary>
		/// Movs the memory register.
		/// </summary>
		/// <param name="assign">The assign.</param>
		private void MovMemoryRegister (Assign assign)
		{
			this.MovMemoryRegister (assign.Assignee, Assembly.GetRegister (assign.Value.Register));
		}

		/// <summary>
		/// Movs the register memory.
		/// </summary>
		/// <param name="assign">The assign.</param>
		private void MovRegisterMemory (Assign assign)
		{
			this.MovRegisterMemory (Assembly.GetRegister (assign.Assignee.Register), assign.Value as Identifier);
		}

		/// <summary>
		/// Movs the memory memory.
		/// </summary>
		/// <param name="assign">The assign.</param>
		private void MovMemoryMemory (Assign assign)
		{
			if (assign.Assignee.SizeType == Operand.InternalSizeType.ValueType) {
				this.assembly.PUSH (R32.ECX);
				this.assembly.PUSH (R32.ESI);
				this.assembly.PUSH (R32.EDI);

				this.MovRegisterOperand (R32.ESI, assign.Value);
				this.MovRegisterOperand (R32.EDI, assign.Assignee);

				string typeName = assign.Assignee.TypeName;

				uint size = (uint) this.method.Engine.GetTypeSize (typeName, 4) / 4;

				this.assembly.MOV (R32.ECX, size);

				this.assembly.CLD ();
				this.assembly.REP ();
				this.assembly.MOVSD ();

				this.assembly.POP (R32.EDI);
				this.assembly.POP (R32.ESI);
				this.assembly.POP (R32.ECX);
				
			} else  if (IsFourBytes (assign.Assignee)) {
				R32Type register = assembly.GetSpareRegister();

				this.MovRegisterMemory (register, assign.Value as Identifier);

				this.MovMemoryRegister (assign.Assignee, register);

				assembly.FreeSpareRegister (register);

			} else if (IsEightBytes (assign.Assignee)) {
				this.MovRegisterMemory (R32.EAX, R32.EDX, assign.Value as Identifier);

				this.MovMemoryRegister (assign.Assignee, R32.EAX, R32.EDX);

			} else
				throw new Exception ("'" + assign + "' not supported.");
		}

		/// <summary>
		/// Movs the memory constant.
		/// </summary>
		/// <param name="memory">The memory.</param>
		/// <param name="constant">The constant.</param>
		private void MovMemoryConstant (Memory memory, UInt32 constant)
		{
			if (constant == 0) {
				R32Type register = assembly.GetSpareRegister();

				assembly.XOR (register, register);

				if (memory is ByteMemory) {
					assembly.MOV (memory as ByteMemory, Assembly.Get8BitRegister (register));

				} else if (memory is WordMemory) {
					assembly.MOV (memory as WordMemory, Assembly.Get16BitRegister (register));

				} else if (memory is DWordMemory) {
					assembly.MOV (memory as DWordMemory, register);

				} else
					throw new Exception ("'" + memory + "' not supported.");

				assembly.FreeSpareRegister (register);

			} else if (memory is ByteMemory) {
				assembly.MOV (memory as ByteMemory, Convert.ToByte (constant));

			} else if (memory is WordMemory) {
				assembly.MOV (memory as WordMemory, Convert.ToUInt16 (constant));

			} else if (memory is DWordMemory) {
				assembly.MOV (memory as DWordMemory, constant);

			} else
				throw new Exception ("'" + memory + "' not supported.");
		}

		/// <summary>
		/// Movs the register register.
		/// </summary>
		/// <param name="assign">The assign.</param>
		private void MovRegisterRegister (Assign assign)
		{
			this.MovRegisterRegister (Assembly.GetRegister (assign.Assignee.Register), Assembly.GetRegister (assign.Value.Register));
		}

		/// <summary>
		/// Movs the register register.
		/// </summary>
		/// <param name="target">The target.</param>
		/// <param name="source">The source.</param>
		private void MovRegisterRegister (R32Type target, R32Type source)
		{
			if (target != source)
				assembly.MOV (target, source);
		}

		/// <summary>
		/// Determines whether [is four bytes] [the specified operand].
		/// </summary>
		/// <param name="operand">The operand.</param>
		/// <returns>
		/// 	<c>true</c> if [is four bytes] [the specified operand]; otherwise, <c>false</c>.
		/// </returns>
		private static bool IsFourBytes (Operand operand)
		{
			if (operand.SizeType == Operand.InternalSizeType.I
					|| operand.SizeType == Operand.InternalSizeType.U
					|| operand.SizeType == Operand.InternalSizeType.I1
					|| operand.SizeType == Operand.InternalSizeType.U1
					|| operand.SizeType == Operand.InternalSizeType.I2
					|| operand.SizeType == Operand.InternalSizeType.U2
					|| operand.SizeType == Operand.InternalSizeType.I4
					|| operand.SizeType == Operand.InternalSizeType.U4
					|| operand.SizeType == Operand.InternalSizeType.S)
				return true;

			return false;
		}

		/// <summary>
		/// Determines whether [is eight bytes] [the specified operand].
		/// </summary>
		/// <param name="operand">The operand.</param>
		/// <returns>
		/// 	<c>true</c> if [is eight bytes] [the specified operand]; otherwise, <c>false</c>.
		/// </returns>
		private static bool IsEightBytes (Operand operand)
		{
			if (operand.SizeType == Operand.InternalSizeType.I8
					|| operand.SizeType == Operand.InternalSizeType.U8
					|| operand.SizeType == Operand.InternalSizeType.R8)
				return true;

			return false;
		}

		/// <summary>
		/// Determines whether the specified operand is signed.
		/// </summary>
		/// <param name="operand">The operand.</param>
		/// <returns>
		/// 	<c>true</c> if the specified operand is signed; otherwise, <c>false</c>.
		/// </returns>
		private static bool IsSigned (Operand operand)
		{
			if (operand.SizeType == Operand.InternalSizeType.I
					|| operand.SizeType == Operand.InternalSizeType.I1
					|| operand.SizeType == Operand.InternalSizeType.I2
					|| operand.SizeType == Operand.InternalSizeType.I4
					|| operand.SizeType == Operand.InternalSizeType.I8)
				return true;

			return false;
		}
	}
}
