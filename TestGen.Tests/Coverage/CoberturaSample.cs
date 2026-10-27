namespace TestGen.Tests.Coverage;

static class CoberturaSample
{
    public const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.36" branch-rate="0.25" version="1.9" timestamp="1700000000" lines-covered="7" lines-valid="19">
          <packages>
            <package name="App.Core" line-rate="0.4" branch-rate="0.25" complexity="0">
              <classes>
                <class name="App.Core.Calculator" filename="/src/App.Core/Calculator.cs" line-rate="0.8" branch-rate="0.5" complexity="3">
                  <methods>
                    <method name="Add" signature="(int, int)" line-rate="1" branch-rate="1" complexity="1">
                      <lines>
                        <line number="10" hits="3" branch="false" />
                        <line number="11" hits="3" branch="false" />
                      </lines>
                    </method>
                    <method name="Divide" signature="(int, int)" line-rate="0.66" branch-rate="0.5" complexity="2">
                      <lines>
                        <line number="15" hits="2" branch="true" condition-coverage="50% (1/2)" />
                        <line number="16" hits="0" branch="false" />
                        <line number="17" hits="2" branch="false" />
                      </lines>
                    </method>
                  </methods>
                  <lines>
                    <line number="10" hits="3" branch="false" />
                    <line number="11" hits="3" branch="false" />
                    <line number="15" hits="2" branch="true" condition-coverage="50% (1/2)" />
                    <line number="16" hits="0" branch="false" />
                    <line number="17" hits="2" branch="false" />
                  </lines>
                </class>
                <class name="App.Core.Calculator/&lt;&gt;c" filename="/src/App.Core/Calculator.cs" line-rate="0" branch-rate="1" complexity="1">
                  <methods>
                    <method name="&lt;Divide&gt;b__1_0" signature="(int)" line-rate="0" branch-rate="1" complexity="1">
                      <lines><line number="20" hits="0" branch="false" /></lines>
                    </method>
                  </methods>
                  <lines><line number="20" hits="0" branch="false" /></lines>
                </class>
                <class name="App.Core.Outer/Nested" filename="/src/App.Core/Outer.cs" line-rate="0" branch-rate="1" complexity="1">
                  <methods>
                    <method name="Run" signature="()" line-rate="0" branch-rate="1" complexity="1">
                      <lines>
                        <line number="5" hits="0" branch="false" />
                        <line number="6" hits="0" branch="false" />
                      </lines>
                    </method>
                  </methods>
                  <lines />
                </class>
                <class name="App.Core.Repository`1" filename="/src/App.Core/Repository.cs" line-rate="0.5" branch-rate="0" complexity="2">
                  <methods>
                    <method name="Get" signature="(int)" line-rate="0.5" branch-rate="0" complexity="2">
                      <lines>
                        <line number="8" hits="1" branch="false" />
                        <line number="9" hits="0" branch="true" condition-coverage="0% (0/2)" />
                      </lines>
                    </method>
                  </methods>
                  <lines />
                </class>
                <class name="App.Core.Order" filename="/src/App.Core/Order.cs" line-rate="0.5" branch-rate="1" complexity="1">
                  <methods>
                    <method name="Validate" signature="()" line-rate="0.5" branch-rate="1" complexity="1">
                      <lines>
                        <line number="3" hits="1" branch="false" />
                        <line number="4" hits="0" branch="false" />
                      </lines>
                    </method>
                  </methods>
                  <lines />
                </class>
                <class name="App.Core.Order" filename="/src/App.Core/Order.Logic.cs" line-rate="0" branch-rate="1" complexity="1">
                  <methods>
                    <method name="Total" signature="()" line-rate="0" branch-rate="1" complexity="1">
                      <lines>
                        <line number="7" hits="0" branch="false" />
                        <line number="8" hits="0" branch="false" />
                      </lines>
                    </method>
                  </methods>
                  <lines />
                </class>
              </classes>
            </package>
            <package name="App.Web" line-rate="0.16" branch-rate="1" complexity="0">
              <classes>
                <class name="Program" filename="/src/App.Web/Program.cs" line-rate="0" branch-rate="1" complexity="1">
                  <methods>
                    <method name="&lt;Main&gt;$" signature="(string[])" line-rate="0" branch-rate="1" complexity="1">
                      <lines><line number="1" hits="0" branch="false" /></lines>
                    </method>
                  </methods>
                  <lines />
                </class>
                <class name="App.Web.Generated.Client" filename="/src/App.Web/obj/Client.g.cs" line-rate="0" branch-rate="1" complexity="1">
                  <methods>
                    <method name="Call" signature="()" line-rate="0" branch-rate="1" complexity="1">
                      <lines><line number="2" hits="0" branch="false" /></lines>
                    </method>
                  </methods>
                  <lines />
                </class>
                <class name="App.Web.Dtos.UserDto" filename="/src/App.Web/Dtos/UserDto.cs" line-rate="0.33" branch-rate="1" complexity="3">
                  <methods>
                    <method name=".ctor" signature="()" line-rate="1" branch-rate="1" complexity="1">
                      <lines><line number="3" hits="1" branch="false" /></lines>
                    </method>
                    <method name="get_Name" signature="()" line-rate="0" branch-rate="1" complexity="1">
                      <lines><line number="5" hits="0" branch="false" /></lines>
                    </method>
                    <method name="set_Name" signature="(string)" line-rate="0" branch-rate="1" complexity="1">
                      <lines><line number="6" hits="0" branch="false" /></lines>
                    </method>
                  </methods>
                  <lines />
                </class>
                <class name="App.Web.Migrations.Init" filename="/src/App.Web/Migrations/2024_Init.cs" line-rate="0" branch-rate="1" complexity="1">
                  <methods>
                    <method name="Up" signature="(MigrationBuilder)" line-rate="0" branch-rate="1" complexity="1">
                      <lines><line number="10" hits="0" branch="false" /></lines>
                    </method>
                  </methods>
                  <lines />
                </class>
                <class name="App.Web.Service/&lt;DoAsync&gt;d__2" filename="/src/App.Web/Service.cs" line-rate="0" branch-rate="1" complexity="1">
                  <methods>
                    <method name="MoveNext" signature="()" line-rate="0" branch-rate="1" complexity="1">
                      <lines><line number="12" hits="0" branch="false" /></lines>
                    </method>
                  </methods>
                  <lines />
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;
}
